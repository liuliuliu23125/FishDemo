using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FishDemo.EditorTools
{
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

            var horizon = AssetDatabase.LoadAssetAtPath<Material>(HorizonMat);
            if (horizon != null)
            {
                W($"Horizon 当前 shader = {horizon.shader.name}");
                var lit = Shader.Find(UrpLit);
                if (lit != null && horizon.shader.name != UrpLit)
                {
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
