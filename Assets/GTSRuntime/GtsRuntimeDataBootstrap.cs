using UnityEngine;
using ProceduralWorlds.GTS;

[DisallowMultipleComponent]
public class GtsRuntimeDataBootstrap : MonoBehaviour
{
    public GTSProfile profile;

    private void Start()
    {
        var gtsMesh = GetComponent<GTSMesh>();
        if (gtsMesh == null)
            return;

        if (gtsMesh.profile == null)
            gtsMesh.profile = profile;

        gtsMesh.Refresh();
    }
}
