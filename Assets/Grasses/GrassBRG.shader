// 草 shader：BRG 链路 + 主光光照 + 顶点风摆 + albedo 贴图/裁剪。
//
// 演进：
//   M1（原版）：纯色、无光照、无阴影、无风摆，目的只是验证 URP 14 + BRG 的
//               DOTS_INSTANCING 链路能跑通。视觉上像塑料片。
//   2026-11-11 早：加 Lambert 主光 + 半球环境光 + 顶点风摆。仍是「无阴影、
//               无贴图、无透光」的简化版，但已有明暗层次与摆动。
//   现在       ：加 albedo 贴图采样 + alpha 裁剪 + per-type tint。
//               花的真实颜色（雏菊白瓣黄芯/三叶草粉/蒲公英黄）在图集像素里，
//               纯 tint 造不出花色——图集是 RGBA、约一半像素透明背景，
//               必须采样 + 裁剪。贴图按 LOD 差异化：LOD0（原型 mesh）有图，
//               LOD1/2（程序化草簇/十字面片）无 UV 语义、走纯色渐变。
//               阴影/透光仍属 M3（Docs/grass_brg_plan.md §6）。
//
// 为什么必须自写：URP 14 全包只有 BRGPicking / ObjectMotionVectors 两个 shader 带
// DOTS_INSTANCING_ON，Lit/Unlit 系列都不支持 BRG（方案 §1.2）。
//
// 版本依赖：URP 14.0.12。DOTS instancing 的宏与 float3x4 还原公式来自
// com.unity.render-pipelines.core 14.0.12 的 ShaderLibrary/UnityDOTSInstancing.hlsl。
// 是否依赖深度图：否。低端降级：无——BRG 需要 raw buffer，Android 上只能跑 Vulkan。
Shader "FishDemo/Grass/BRGMinimal"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.16, 0.34, 0.09, 1)
        _TipColor ("Tip Color", Color) = (0.42, 0.62, 0.22, 1)
        _AmbientTint ("Ambient Tint", Color) = (0.55, 0.60, 0.45, 1)
        _WindSpeed ("Wind Speed", Float) = 1.2
        _WindStrength ("Wind Strength (m)", Float) = 0.08
        // 有贴图时走 albedo 采样（花的颜色在图集里）；为空时退回纯色渐变。
        // 绑定的是 RGBA 图集，花/草各占一块 UV 区域，贴图本身即颜色来源。
        [NoScaleOffset] _MainTex ("Albedo (RGB) A(Clip)", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Toggle(_USE_ALBEDO_TEX)] _UseAlbedoTex ("Use Albedo Texture", Float) = 0
        // per-类型染色。图集已是最终颜色，这里只做类型间轻微区分
        // （如枯草块偏黄）。必须声明在 Properties 里，否则材质序列化与
        // HasProperty 都不认它（代码 SetColor 虽能生效，但材质面板看不到、
        // 存档也可能丢）。
        _TypeTint ("Type Tint", Color) = (1,1,1,1)
        // per-类型基础尺寸（由 Gaia detailPrototype 的 min/max 宽高换算）。
        //   x = 水平倍率  y = 竖直倍率  z = 网格原始高度（米，用于归一化 bladeT）
        // 为什么放在材质而不是 per-instance：7 种类型的真实尺寸差 10 倍以上
        // （枯草块 5~8 倍、雏菊 1~4 倍，见 Logs/terrain-detail-probe.txt），
        // 而 GPU buffer 只有 8 位的统一缩放抖动。类型尺寸天然是「一类型一份」，
        // 材质本来就按类型分段，不增加任何带宽。
        _TypeScaleBase ("Type Scale Base (xz, y, meshHeight, unused)", Vector) = (1,1,1,0)
        [HideInInspector] _GrassPos ("Grass Pos", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GrassNormalScale ("Grass NormalScale", Vector) = (0, 1, 0, 1)
    }

    SubShader
    {
        Tags
        {
            // 有贴图后花边缘靠 alpha 裁剪，必须声明 TransparentCutout，
            // 否则 DepthPrepass/对象拾取等不裁 alpha 的 pass 会把透明区域画成实心块。
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "BRGForward"
            // URP 的 opaque pass 用 ShaderTagId("UniversalForward") 调 DrawRenderers，
            // 引擎在该调用内下发 BRG 的 draw command。tag 写错 = 草完全不出现。
            Tags { "LightMode" = "UniversalForward" }

            // M1 用单面片，双面都画，省得排查背面剔除导致的"看不见"。
            Cull Off
            // alpha 裁剪要求 ZWrite 保持 On（AlphaTest 队列不影响 early-z）。
            ZWrite On
            ZTest LEqual
            // 刻意不开 AlphaToMask：工程 MSAA=1（Assets/Settings/UniversalRP.asset:28），
            // 无 MSAA 时 alpha-to-coverage 退化成近似全裁，近景草被切成亮绿碎屑
            // （实测截图确认）。关掉后靠 clip 硬边；这类像素风图集本来就硬边。
            // 将来若把 MSAA 提到 4，可以再启用 A2M 换边缘抖动平滑。

            HLSLPROGRAM

            // BRG 的硬性要求：target 4.5 + 排除 GLES。
            // 依据 URP 14 的 Shaders/BRGPicking.shader:31-32。
            #pragma target 4.5
            #pragma exclude_renderers gles

            // 与 URP 自带 shader 同一写法（ShaderLibrary/DOTS.hlsl:5），
            // 下划线是"关闭"变体，保证非 DOTS 路径也能编译。
            #pragma multi_compile _ DOTS_INSTANCING_ON

            // 主光。阴影暂不做（M3），所以不声明 _MAIN_LIGHT_SHADOWS 系列变体，
            // 省掉每个阴影变体的编译成本与采样开销。
            // 雾必须有：远处草不被雾吞掉的话，地平线会出现一条生硬的绿色边界。
            #pragma multi_compile_fog

            // 贴图开关。不用 multi_compile 是因为它只会产生「有/无」两态，
            // 而材质上是否存在贴图由 Manager 按 LOD 决定（LOD0 有图、LOD1/2 无图），
            // 用 shader_feature 让同一 shader 能以不同变体出现在不同材质上。
            #pragma shader_feature_local _USE_ALBEDO_TEX

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // SRP Batcher 兼容所需的 UnityPerMaterial。
            // 本 shader 走 BRG，不进 SRP Batcher，但保持同一约定。
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _AmbientTint;
                // per-类型染色：图集已是最终颜色，这里只做类型间轻微区分
                // （如枯草块偏黄）。默认白 = 不改色。
                //
                // 为什么不做成 per-instance：batch 本来就按 detail 类型分段
                // （一个 draw command 只能绑一个 mesh → 同 batch 必然同类型），
                // 「不同 Type 不同 tint」用每类型一个材质即可实现，
                // GPU buffer 保持 32B/实例不变，不增加上传带宽。
                // 磁盘 GrassInstance.Color（RGBA8）字段保留未用——
                // 将来要做逐实例 healthy/dry 颜色变化时再升级为第三段上传。
                float4 _TypeTint;
                // x=水平倍率 y=竖直倍率 z=网格原始高度（米）
                float4 _TypeScaleBase;
                float  _WindSpeed;
                float  _WindStrength;
                float  _Cutoff;
                float  _UseAlbedoTex;
            CBUFFER_END

            // ── 贴图 ──
            // RGBA 图集：花瓣/花蕊的真实颜色都在图集像素里，
            // alpha 通道是透明背景（实测约 49% alpha==0），必须裁剪。
            // 为什么放在 CBUFFER 外：贴图声明不能进 UnityPerMaterial。
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // ── per-instance 属性（BRG / DOTS instancing）──
            // 两个 float4 = 32B/实例，与磁盘 GrassInstance 的 32B 一一对应。
            //
            // 为什么不直接用 unity_ObjectToWorld（48B 的 float3x4）：
            // 烘焙产物存的是语义数据（位置/朝向/缩放/法线），把它在 CPU 展开成矩阵
            // 再上传，等于每实例多传 16B，且烘焙后无法再调缩放/LOD 策略。
            // 这里传语义数据，shader 按需重建变换，带宽省 33%。
            //
            // 字段映射（必须与 GrassBRGManager.UploadInstanceData 严格一致）：
            //   _GrassPos          = (x, y, z, yaw01)          yaw01 ∈ [0,1) → yaw 弧度
            //   _GrassNormalScale  = (nx, ny, nz, scale)       scale ∈ [0.5,1.5]
            //
            // 必须包在 #ifdef UNITY_DOTS_INSTANCING_ENABLED 里：
            // 这套宏只在 DOTS 变体下定义（core 的 UnityInstancing.hlsl:260-285，
            // 非 DOTS 分支里这些宏是空的，UnityDOTSInstancing.hlsl 整体也不被包含）。
            // URP 的 LitInput.hlsl:37 同样是这么包的。
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(UserPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _GrassPos)
                UNITY_DOTS_INSTANCED_PROP(float4, _GrassNormalScale)
            UNITY_DOTS_INSTANCING_END(UserPropertyMetadata)

            // 读取后缓存到静态变量，再把名字重定向到缓存值。
            // 这是 URP 的标准做法（见 UnlitInput.hlsl:20-36），原因是
            // UNITY_ACCESS_DOTS_INSTANCED_PROP 会展开成多次 buffer 读取，
            // 直接在 vertex 里内联调用会重复读；先读一次再重定向更高效，
            // 也避免宏内的 token 拼接在自定义名字上出现意外。
            static float4 unity_DOTS_Sampled_GrassPos;
            static float4 unity_DOTS_Sampled_GrassNormalScale;

            void SetupDOTSGrassPropertyCaches()
            {
                unity_DOTS_Sampled_GrassPos =
                    UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, _GrassPos);
                unity_DOTS_Sampled_GrassNormalScale =
                    UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, _GrassNormalScale);
            }

            #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
            #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSGrassPropertyCaches()

            #define _GrassPos          unity_DOTS_Sampled_GrassPos
            #define _GrassNormalScale  unity_DOTS_Sampled_GrassNormalScale
            #else
            // 非 DOTS 变体：没有 per-instance 数据，用常量兜底让 shader 可编译。
            // 本工程的草只经 BRG 提交，实际渲染不会走到这条路径。
            static const float4 _GrassPos = float4(0, 0, 0, 0);
            static const float4 _GrassNormalScale = float4(0, 1, 0, 1);
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                // 世界法线（草面片朝上，用实例的地表法线而非网格法线）
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                // 叶片高度 0(根)..1(尖)，用于基础色→尖色的渐变
                float  bladeT : TEXCOORD3;
                float  fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                // 这一句是整条链路的关键：
                // UNITY_SETUP_INSTANCE_ID 在 DOTS 路径下会调用 SetupDOTSVisibleInstancingData()，
                // 把 SV_InstanceID 映射成可见实例索引，后续读 per-instance 属性才拿得到数据。
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // ── 取 per-instance 数据 ──
                // DOTS 变体下 _GrassPos / _GrassNormalScale 已被上面的 #define
                // 重定向到缓存变量；非 DOTS 变体下它们是 UnityPerMaterial 里的
                // 材质属性，读出来是材质值（全 0 / 单位），仅为编译兜底。
                float4 pos = _GrassPos;
                float4 nscale = _GrassNormalScale;

                // ── 重建实例变换 ──
                // 绕 Y 轴旋转 + 均匀缩放，再把「上方向」倾斜到地表法线。
                // 草网格的原点在根部、+Y 向上，所以基向量取：
                //   up    = 法线（贴合坡面，陡坡上不会插进地里）
                //   right = up × forwardHint
                //   fwd   = right × up
                float yaw = pos.w * 6.2831853f;
                float s = nscale.w;

                float3 up = normalize(nscale.xyz);
                float3 hint = float3(cos(yaw), 0.0, sin(yaw));
                // 法线与 hint 平行时 cross 退化，兜底用 X 轴。
                float3 right = cross(hint, up);
                float rightLen = length(right);
                right = rightLen > 1e-4f ? right / rightLen : float3(1, 0, 0);
                float3 fwd = cross(up, right);

                // ── 类型尺寸 ──
                // Gaia 的 detailPrototype 给了每类的 min/max 宽高（枯草块 5~8 倍、
                // 主草 1~2 倍、雏菊 1~4 倍）。烘焙时无法逐实例存两份尺寸
                // （GPU buffer 只有 8 位 scale），所以按类型拆成「基础倍率 ×
                // 逐实例抖动」：基础倍率走材质属性（一类型一份，零带宽），
                // 抖动仍用现有的 ScaleQ。
                //
                // 水平/竖直分开：花与草簇的宽高比差别很大，等比缩放会把花拉变形。
                // 网格原始高度从 _TypeScaleBase.z 取，bladeT 归一化必须用它而不是
                // 硬编码 1m——雏菊网格只有 0.15m 高，按 1m 算的话 bladeT 永远接近 0，
                // 根部色的渐变就等于没生效。
                float scaleXZ = s * max(1e-4f, _TypeScaleBase.x);
                float scaleY  = s * max(1e-4f, _TypeScaleBase.y);
                float meshH   = max(1e-4f, _TypeScaleBase.z);

                float3 local = float3(input.positionOS.x * scaleXZ,
                                      input.positionOS.y * scaleY,
                                      input.positionOS.z * scaleXZ);

                // ── 风摆 ──
                // 权重取叶片在网格上的归一化高度（顶点 y / 网格真实高度）。
                // 根部不动、越靠尖摆得越多，否则整株草会像刚体一样平移。
                //
                // 相位用实例世界位置做偏移，避免整片草同频同步摆动
                // （那样看起来像一块布在呼吸，非常假）。取 x+z 的点积即可，
                // 不同位置的草相位自然错开。
                float bladeT = saturate(input.positionOS.y / meshH);
                float phase = (pos.x + pos.z) * 0.35f;
                float t = _Time.y * _WindSpeed + phase;
                // 两个不同频率叠加，避免纯正弦的机械感
                float sway = sin(t) * 0.7f + sin(t * 2.3f + 1.7f) * 0.3f;
                // 位移要按竖直倍率同步放大，否则大个的枯草块根部会晃到土里
                float3 windOffset = hint * (sway * _WindStrength * bladeT * bladeT * scaleY);
                local += windOffset;

                float3 world = pos.xyz + right * local.x + up * local.y + fwd * local.z;

                output.positionWS = world;
                output.positionCS = TransformWorldToHClip(world);
                // 草面片本身没有有意义的网格法线（是十字面片），
                // 直接用实例携带的地表法线：坡面上的草会随坡面明暗变化。
                output.normalWS = up;
                // UV 直接用网格自带的：精灵格映射已在构建 mesh 时烘焙进 UV
                // （见 GrassBRGManager.BuildScaledBillboard），运行时不需要再变换。
                output.uv = input.uv;
                output.bladeT = bladeT;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input, bool front_facing : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half3 albedo;
                #ifdef _USE_ALBEDO_TEX
                // ── 有贴图：花/草的真实颜色来自图集像素 ──
                // albedo 图集是主草类型的颜色来源（雏菊白瓣黄芯、
                // 三叶草粉花、蒲公英黄花都在图集里）。tint 做轻微的
                // 实例间变化（默认白 = 不改色），不承担"造出花色"的职责。
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                // alpha 裁剪：图集约一半像素是透明背景，不裁会把透明区画成实心块。
                // 硬边裁剪（未开 AlphaToMask，原因见 Pass 顶部注释）。
                clip(tex.a - _Cutoff);
                albedo = tex.rgb * _TypeTint;
                #else
                // ── 无贴图：根部深绿 → 尖端浅绿 ──
                // 这个渐变是"看得出是草"而非"一片绿"的关键。
                // 用 sqrt 让浅色集中在尖端。tint 乘在渐变上（LOD1/2、
                // 以及未绑定贴图的类型）。默认 _TypeTint = 白 = 不改色。
                albedo = lerp(_BaseColor.rgb, _TipColor.rgb, sqrt(input.bladeT)) * _TypeTint;
                #endif

                // ── 主光 Lambert ──
                // 双面渲染（Cull Off），背面法线要翻转，否则从背面看是死黑。
                float3 n = normalize(input.normalWS);
                if (!front_facing) n = -n;

                Light mainLight = GetMainLight();

                // ── 叶片光照：不要用平面法线做 Lambert ──
                //
                // 为什么：草/花的几何是竖直面片（十字交叉），而这里传入的
                // normalWS 是**地表法线（朝上）**。用 dot(up, lightDir) 意味着
                // 太阳越低整片草越黑——实测俯角 25° 的场景里草只剩 20% 亮度，
                // 花有颜色但暗得看不见。真实草叶是薄片，光从侧面来也会透，
                // 物理上近似「半球漫反射」而不是平面 Lambert。
                //
                // 做法：混入一个「面向相机」的方向，让叶子在任意视角都拿到
                // 合理的光照量。权重 0.6 是经验值：完全朝相机会失去明暗层次，
                // 完全朝上又会在低太阳角下过暗。
                float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                float3 lightDir = normalize(mainLight.direction);
                // 把法线往「朝向相机」偏，并夹到上半球，避免朝下的法线拿到负光
                float3 leafNormal = normalize(lerp(n, viewDirWS, 0.6));
                leafNormal = normalize(leafNormal + float3(0, 0.5, 0));
                float ndotl = saturate(dot(leafNormal, lightDir));
                // 抬高下界的「包裹光照」：叶片背光面不应全黑，真实草叶会透光。
                // 这是「草看起来有生命」的关键，比纯 Lambert 更接近参考效果。
                ndotl = ndotl * 0.75f + 0.25f;
                half3 direct = albedo * mainLight.color * ndotl * mainLight.distanceAttenuation;

                // ── 环境光 ──
                // 纯色草最假的地方就是背光面全黑，补一层常量环境光后
                // 叶片背光面也能看清轮廓。
                // 不用 SampleSH：BRG 下的 draw command 不走常规 per-object 流程，
                // SH 系数可能未绑定，读到 0/NaN 会让整片草被丢弃（实测踩过）。
                half3 ambient = albedo * _AmbientTint.rgb;

                half3 color = direct + ambient;

                // 远处融进雾里。
                // 用 GetFogFactor 的形式而非 MixFog：MixFog 在 fog 变体未启用时
                // 依赖 unity_FogParams，BRG 路径下该 uniform 可能未绑定，
                // 会把颜色算成极端值（整片草被雾色吞掉，实测过一次）。
                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                color = MixFog(color, input.fogFactor);
                #endif

                return half4(color, 1.0);
            }

            ENDHLSL
        }
    }

    FallBack Off
}