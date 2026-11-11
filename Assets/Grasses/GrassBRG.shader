// 草 shader：BRG 链路 + 主光光照 + 顶点风摆。
//
// 演进：
//   M1（原版）：纯色、无光照、无阴影、无风摆，目的只是验证 URP 14 + BRG 的
//               DOTS_INSTANCING 链路能跑通。视觉上像塑料片。
//   现在       ：加 Lambert 主光 + 半球环境光 + 顶点风摆。仍然是「无阴影、
//               无贴图、无透光」的简化版，但已有明暗层次与摆动，
//               足以支撑「贴地看草」的观察需求。
//               阴影/透光/贴图仍属 M3（Docs/grass_brg_plan.md §6）。
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
        [HideInInspector] _GrassPos ("Grass Pos", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GrassNormalScale ("Grass NormalScale", Vector) = (0, 1, 0, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
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
            ZWrite On
            ZTest LEqual

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
                float  _WindSpeed;
                float  _WindStrength;
            CBUFFER_END

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

                float3 local = input.positionOS.xyz * s;

                // ── 风摆 ──
                // 权重取叶片在网格上的归一化高度（顶点 y / 标称 1m）。
                // 根部不动、越靠尖摆得越多，否则整株草会像刚体一样平移。
                //
                // 相位用实例世界位置做偏移，避免整片草同频同步摆动
                // （那样看起来像一块布在呼吸，非常假）。取 x+z 的点积即可，
                // 不同位置的草相位自然错开。
                float bladeT = saturate(input.positionOS.y);
                float phase = (pos.x + pos.z) * 0.35f;
                float t = _Time.y * _WindSpeed + phase;
                // 两个不同频率叠加，避免纯正弦的机械感
                float sway = sin(t) * 0.7f + sin(t * 2.3f + 1.7f) * 0.3f;
                float3 windOffset = hint * (sway * _WindStrength * bladeT * bladeT);
                local += windOffset;

                float3 world = pos.xyz + right * local.x + up * local.y + fwd * local.z;

                output.positionWS = world;
                output.positionCS = TransformWorldToHClip(world);
                // 草面片本身没有有意义的网格法线（是十字面片），
                // 直接用实例携带的地表法线：坡面上的草会随坡面明暗变化。
                output.normalWS = up;
                output.uv = input.uv;
                output.bladeT = bladeT;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input, bool front_facing : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // 根部深绿 → 尖端浅绿：没有贴图时，这个渐变是"看得出是草"
                // 而非"一片绿"的关键。用 sqrt 让浅色集中在尖端。
                half3 albedo = lerp(_BaseColor.rgb, _TipColor.rgb, sqrt(input.bladeT));

                // ── 主光 Lambert ──
                // 双面渲染（Cull Off），背面法线要翻转，否则从背面看是死黑。
                float3 n = normalize(input.normalWS);
                if (!front_facing) n = -n;

                Light mainLight = GetMainLight();
                float ndotl = saturate(dot(n, mainLight.direction));
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
