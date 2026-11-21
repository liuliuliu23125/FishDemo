using System.IO;
using System.Text;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

public static class RainRippleSetup
{
    private const string Tag = "[RainRippleSetup]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string WaterNodeName = "Crest Lake";
    private const string TexturePath = "Assets/Textures/Water/RainRipples_Temp.png";
    private const string ResultPath = "Logs/RainRipple/setup.txt";

    public static void Apply() { Run("apply"); }
    public static void Remove() { Run("remove"); }
    public static void Report() { Run("report"); }
    public static void PreviewOn() { Run("preview-on"); }
    public static void PreviewOff() { Run("preview-off"); }

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s)
        {
            Debug.Log($"{Tag} {s}");
            sb.AppendLine(s);
        }

        var ok = true;
        try
        {
            Directory.CreateDirectory("Logs/RainRipple");
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            var root = GameObject.Find(WaterNodeName);
            if (root == null)
            {
                var water = Object.FindObjectOfType<WaterRenderer>();
                if (water != null) root = water.gameObject;
            }

            if (root == null)
            {
                W($"FAIL：场景里找不到 {WaterNodeName} 或 WaterRenderer");
                Flush(sb, 2);
                return;
            }

            var controller = root.GetComponent<RainRippleController>();
            W($"模式 = {mode}  场景 = {WorldScenePath}  节点 = {root.name}");

            if (mode == "remove")
            {
                if (controller != null)
                {
                    Object.DestroyImmediate(controller);
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    W("已移除 RainRippleController");
                }
                else
                {
                    W("RainRippleController 不存在，无需移除");
                }
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (mode == "apply" || mode == "preview-on" || mode == "preview-off")
            {
                if (texture == null)
                {
                    W($"FAIL：找不到雨纹贴图 {TexturePath}");
                    Flush(sb, 2);
                    return;
                }

                if (controller == null)
                {
                    controller = root.AddComponent<RainRippleController>();
                    if (controller == null)
                    {
                        W("FAIL：AddComponent<RainRippleController> 失败");
                        Flush(sb, 2);
                        return;
                    }
                    W("已添加 RainRippleController");
                }
            }

            if (mode == "apply" || mode == "preview-off")
            {
                controller.NormalTexture = texture;
                controller.AutoFromEnviro = true;
                controller.ManualIntensity = 0f;
                controller.BlendSeconds = 1.5f;
                controller.Scale = 2f;
                controller.Speed = 0.05f;
                controller.MaxIntensity = 0.7f;
            }
            else if (mode == "preview-on")
            {
                controller.NormalTexture = texture;
                controller.AutoFromEnviro = false;
                controller.ManualIntensity = 1f;
                controller.BlendSeconds = 0.05f;
                controller.Scale = 2f;
                controller.Speed = 0.05f;
                controller.MaxIntensity = 0.7f;
            }

            if (mode != "report")
            {
                EditorUtility.SetDirty(controller);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
            }

            W($"控制器存在 = {controller != null}");
            W($"贴图 = {(controller != null && controller.NormalTexture != null ? AssetDatabase.GetAssetPath(controller.NormalTexture) : "null")}");
            W($"AutoFromEnviro = {(controller != null && controller.AutoFromEnviro)}");
            W($"ManualIntensity = {(controller != null ? controller.ManualIntensity : 0f)}");

            if (controller == null || controller.NormalTexture == null) ok = false;
            if (mode == "report" && texture == null) W($"WARN：贴图 {TexturePath} 缺失");

            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(sb, ok ? 0 : 2);
        }
        catch (System.Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory("Logs/RainRipple");
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
