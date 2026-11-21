using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class RainRippleVerify
{
    private const string Tag = "[RainRipple]";
    private const string WaterMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat";
    private const string IncludePath = "Packages/com.waveharmonic.crest/Runtime/Shaders/Surface/RainRipples.hlsl";
    private const string FragmentPath = "Packages/com.waveharmonic.crest/Runtime/Shaders/Surface/Fragment.hlsl";
    private const string ControllerType = "FishDemo.Gameplay.RainRippleController, Assembly-CSharp";

    public static void Run()
    {
        var ok = true;

        void Check(bool condition, string message)
        {
            Debug.Log($"{Tag} {(condition ? "PASS" : "FAIL")} {message}");
            if (!condition) ok = false;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
        Check(
            material != null && material.shader != null && material.shader.name == "Crest/Water",
            $"Water.mat shader = {(material != null && material.shader != null ? material.shader.name : "null")}");

        Check(
            File.Exists(IncludePath) && File.ReadAllText(IncludePath).Contains("RainRippleNormal"),
            "RainRipples.hlsl 包含 RainRippleNormal");
        Check(
            File.Exists(FragmentPath) && File.ReadAllText(FragmentPath).Contains("RainRippleNormal(i_UndisplacedXZ, i_Underwater)"),
            "Fragment.hlsl 已接入 RainRippleNormal");
        Check(Type.GetType(ControllerType) != null, "RainRippleController 已编译");

        if (material != null && material.shader != null)
        {
            Debug.Log($"{Tag} shader={material.shader.name} passCount={material.shader.passCount}");
            for (var i = 0; i < material.shader.passCount; i++)
            {
                ShaderUtil.CompilePass(material, i, false);
                ShaderUtil.CompilePass(material, i, true);
            }

            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
            {
                Debug.Log($"{Tag} {message.platform} {message.file}:{message.line} {message.message}");
            }

            Check(!ShaderUtil.ShaderHasError(material.shader), "Crest/Water 全部 pass 无 shader 错误");
        }

        Debug.Log($"{Tag} ==== 结论：{(ok ? "PASS" : "FAIL")} ====");
        EditorApplication.Exit(ok ? 0 : 2);
    }
}
