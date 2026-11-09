using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 把水面相关残留的 BuiltIn/Legacy 材质改为 URP 等价 shader。
    ///
    /// ── 为什么单独处理这两个 ──
    /// 全工程扫描后，真正被 World 场景（含 36 个 chunk）引用、且 shader 不可用的只有 2 个：
    ///   1. Gaia Water/Content Resources/Material/Horizon Water Material.mat
    ///      → m_Shader 指向 Unity 内置 "Standard"（fileID 46 / guid 0000...f000）
    ///      被 Underwater Horizon.prefab 引用 5 次，是玩家靠近水面时看到的远景层。
    ///   2. .../Underwater Particles Material.mat
    ///      → "Legacy Shaders/Particles/Alpha Blended"（fileID 203）
    ///      被 Underwater Particles Effects.prefab 引用，是水下气泡粒子。
    /// 其余 46 个非 URP 材质都在 GeNa/SECTR 的 Demo 资产里，运行时不参与渲染，
    /// 改它们只会污染第三方资产，因此不动。
    ///
    /// ── 为什么改材质而不是换 prefab ──
    /// 这些材质被多个 prefab 引用（含已导入的 URP 包内部），
    /// 换 prefab 会打断现有引用链；直接把材质的 shader 指到 URP 版，
    /// 所有引用方一次性生效，且属性名大部分兼容。
    ///
    /// 用法：
    ///   - 菜单 FishDemo/Gaia/③ 修复水面残留 BuiltIn 材质
    ///   - 批处理：-executeMethod FishDemo.EditorTools.FixWaterMaterials.Setup
    /// </summary>
    public static class FixWaterMaterials
    {
        private const string Tag = "[FixWater]";

        private const string HorizonMat = "Assets/Procedural Worlds/Packages - Install/Gaia Water/Content Resources/Material/Horizon Water Material.mat";
        private const string ParticlesMat = "Assets/Procedural Worlds/Packages - Install/Gaia Water/Content Resources/Material/Underwater Particles Material.mat";
        private const string UrpLit = "Universal Render Pipeline/Lit";
        private const string UrpParticlesUnlit = "Universal Render Pipeline/Particles/Unlit";

        [MenuItem("FishDemo/Gaia/③ 修复水面残留 BuiltIn 材质")]
        public static void Setup()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            int fixedCount = 0;

            // ---- 1. Horizon Water Material：Standard -> URP/Lit ----
            var horizon = AssetDatabase.LoadAssetAtPath<Material>(HorizonMat);
            if (horizon != null)
            {
                W($"Horizon 当前 shader = {horizon.shader.name}");
                var lit = Shader.Find(UrpLit);
                if (lit != null && horizon.shader.name != UrpLit)
                {
                    // 先备份关键属性，换 shader 后回填。
                    // Standard 与 URP/Lit 的主贴图属性名都是 _MainTex，颜色都是 _Color，
                    // 但金属/光滑度命名不同（_Glossiness vs _Smoothness），这里一并搬运。
                    Texture mainTex = horizon.HasProperty("_MainTex") ? horizon.GetTexture("_MainTex") : null;
                    Color color = horizon.HasProperty("_Color") ? horizon.GetColor("_Color") : Color.white;
                    float gloss = horizon.HasProperty("_Glossiness") ? horizon.GetFloat("_Glossiness")
                                : horizon.HasProperty("_Smoothness") ? horizon.GetFloat("_Smoothness") : 0.5f;

                    horizon.shader = lit;

                    if (mainTex != null && horizon.HasProperty("_BaseMap")) horizon.SetTexture("_BaseMap", mainTex);
                    if (mainTex != null && horizon.HasProperty("_MainTex")) horizon.SetTexture("_MainTex", mainTex);
                    if (horizon.HasProperty("_BaseColor")) horizon.SetColor("_BaseColor", color);
                    if (horizon.HasProperty("_Color")) horizon.SetColor("_Color", color);
                    if (horizon.HasProperty("_Smoothness")) horizon.SetFloat("_Smoothness", gloss);

                    EditorUtility.SetDirty(horizon);
                    fixedCount++;
                    W($"  已改为 {UrpLit}，回填 mainTex={mainTex?.name ?? "null"} color={color} smoothness={gloss:F2}");
                }
            }
            else W($"未找到 {HorizonMat}");

            // ---- 2. Underwater Particles Material：Legacy Particles -> URP Particles Unlit ----
            var particles = AssetDatabase.LoadAssetAtPath<Material>(ParticlesMat);
            if (particles != null)
            {
                W($"Underwater Particles 当前 shader = {particles.shader.name}");
                bool alreadyUrp = particles.shader.name.StartsWith("Universal Render Pipeline/");
                if (!alreadyUrp)
                {
                    var urpUnlit = Shader.Find(UrpParticlesUnlit);
                    if (urpUnlit != null)
                    {
                        Texture tex = particles.HasProperty("_MainTex") ? particles.GetTexture("_MainTex") : null;
                        Color tint = particles.HasProperty("_TintColor") ? particles.GetColor("_TintColor")
                                   : particles.HasProperty("_Color") ? particles.GetColor("_Color") : Color.white;

                        particles.shader = urpUnlit;

                        if (tex != null && particles.HasProperty("_BaseMap")) particles.SetTexture("_BaseMap", tex);
                        if (tex != null && particles.HasProperty("_MainTex")) particles.SetTexture("_MainTex", tex);
                        if (particles.HasProperty("_BaseColor")) particles.SetColor("_BaseColor", tint);

                        EditorUtility.SetDirty(particles);
                        fixedCount++;
                        W($"  已改为 {UrpParticlesUnlit}，回填 tex={tex?.name ?? "null"} tint={tint}");
                    }
                }
                else W("  已是 URP，跳过");
            }
            else W($"未找到 {ParticlesMat}");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ---- 3. 复查：全工程再扫一次 ----
            var remain = AssetDatabase.FindAssets("t:Material")
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null && m.shader != null)
                .Where(m => m.shader.name == "Standard" && AssetDatabase.GetAssetPath(m).Contains("Gaia Water"))
                .Select(AssetDatabase.GetAssetPath)
                .ToList();
            W($"Gaia Water 目录下仍为 Standard 的材质：{remain.Count}");

            W($"本次修改材质数 = {fixedCount}");
            System.IO.Directory.CreateDirectory("Logs/Gaia");
            System.IO.File.WriteAllText("Logs/Gaia/fix-water.txt", sb.ToString());
            EditorApplication.Exit(0);
        }
    }
}
