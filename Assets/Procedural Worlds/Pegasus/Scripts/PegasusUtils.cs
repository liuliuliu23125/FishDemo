using UnityEngine;

namespace Pegasus
{
    public class PegasusUtils
    {
        /// <summary>
        /// Unity Version independent Instance ID. 
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public static int GetInstanceIdVersionFix(UnityEngine.Object obj)
        {
#if UNITY_6000_4_OR_NEWER
            //using the hashcode of the newer GetEntityId here as this seems to be closer to how 
            //the older Instance ID was used
            return obj.GetEntityId().GetHashCode();
#else
            return obj.GetInstanceID();
#endif
        }
    }
}
