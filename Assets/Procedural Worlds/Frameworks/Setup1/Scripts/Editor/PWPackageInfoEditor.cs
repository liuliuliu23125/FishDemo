using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_6_OR_NEWER
using UnityEditor.AssetPackage;
#endif

namespace ProceduralWorlds.Setup
{
    [CustomEditor(typeof(PWPackageInfo))]
    public class PWPackageInfoEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            PWPackageInfo pWPackageInfo = (PWPackageInfo)target;
            base.OnInspectorGUI();
            if (System.IO.Directory.Exists(Setup.SetupUtils.GetAssetPath("Dev Utilities")))
            {
                if (GUILayout.Button("Create Install Source Package"))
                {
                    string targetPath = "";
                    //Take the already existing package as target path, otherwise use the scriptable object as orientation for the path / file name
                    if (pWPackageInfo.m_unityPackage != null)
                    {
                        targetPath = AssetDatabase.GetAssetPath(pWPackageInfo.m_unityPackage);
                    }
                    else
                    {
                        targetPath = AssetDatabase.GetAssetPath(pWPackageInfo).Replace(".asset", ".unitypackage");
                    }
#if UNITY_6000_6_OR_NEWER

                    ExportPackageParameters exportPackageParameters = new ExportPackageParameters()
                    {
                        AssetPathNames = new string[1] { SetupUtils.GetInstallRootPath() + "/" + pWPackageInfo.m_installFolder },
                        FileName = targetPath,
                        Flags = ExportPackageOptions.Recurse
                    };

                    UnityEditor.AssetPackage.Package.Export(exportPackageParameters);
#else

                    AssetDatabase.ExportPackage(SetupUtils.GetInstallRootPath() + "/" + pWPackageInfo.m_installFolder, targetPath,
        ExportPackageOptions.Recurse);
#endif



                }
            }
        }
    }
}
