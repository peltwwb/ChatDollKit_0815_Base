using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using uLipSync;

namespace ChatdollKit.Model
{
    public class uLipSyncHelper : MonoBehaviour, ILipSyncHelper
    {
        public void ResetViseme()
        {
            try
            {
                var mc = gameObject.GetComponent<ModelController>();
                var avatar = mc != null ? mc.AvatarModel : null;
                if (avatar == null) return;

                var smr = AvatarUtility.GetFacialSkinnedMeshRenderer(avatar);
                if (smr == null || smr.sharedMesh == null) return;

                // Zero out common viseme blendshapes (A, I, U, E, O)
                var map = GetBlendShapeMap(avatar);
                foreach (var kv in map)
                {
                    if (kv.Value >= 0)
                    {
                        smr.SetBlendShapeWeight(kv.Value, 0f);
                    }
                }
            }
            catch { }
        }

        public virtual void ConfigureViseme(GameObject avatarObject)
        {
            // Get BlendShapeMap for viseme
            var blendShapeMap = GetBlendShapeMap(avatarObject);

            // Get/Add uLipSyncBlendShape
            var uLipSyncBlendShape = gameObject.GetComponent<uLipSyncBlendShape>();
            if (uLipSyncBlendShape == null)
            {
                uLipSyncBlendShape = gameObject.AddComponent<uLipSyncBlendShape>();
            }

            // Configure uLipSyncBlendShape
            uLipSyncBlendShape.skinnedMeshRenderer = AvatarUtility.GetFacialSkinnedMeshRenderer(avatarObject);

            // Apply blend shapes
            uLipSyncBlendShape.blendShapes.Clear();
            foreach (var map in blendShapeMap)
            {
                uLipSyncBlendShape.blendShapes.Add(new uLipSyncBlendShape.BlendShapeInfo() { phoneme = map.Key, index = map.Value, maxWeight = 1 });
            }

#if UNITY_EDITOR
            // Get/Add uLipSync
            var uLipSyncMain = gameObject.GetComponent<uLipSync.uLipSync>();
            if (uLipSyncMain == null)
            {
                uLipSyncMain = gameObject.AddComponent<uLipSync.uLipSync>();
            }

            // Add listener
            UnityEditor.Events.UnityEventTools.AddPersistentListener(uLipSyncMain.onLipSyncUpdate, uLipSyncBlendShape.OnLipSyncUpdate);

            // Set profile
            var profiles = UnityEditor.AssetDatabase.FindAssets("-Profile-Female");
            if (profiles.Length > 0)
            {
                uLipSyncMain.profile = UnityEditor.AssetDatabase.LoadAssetAtPath<Profile>(UnityEditor.AssetDatabase.GUIDToAssetPath(profiles.First()));
            }
#endif
        }

        protected virtual Dictionary<string, int> GetBlendShapeMap(GameObject avatarObject)
        {
            var mesh = AvatarUtility.GetFacialSkinnedMeshRenderer(avatarObject).sharedMesh;
            var blendShapeMap = new Dictionary<string, int>()
            {
                { "A", 0 }, { "I", 0 }, { "U", 0 }, { "E", 0 }, { "O", 0 }, { "N", -1 }, { "-", -1 }
            };

            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                if (mesh.GetBlendShapeName(i).Contains("vrc.v_aa"))
                {
                    blendShapeMap["A"] = i;
                }
                else if (mesh.GetBlendShapeName(i).Contains("vrc.v_ih"))
                {
                    blendShapeMap["I"] = i;
                }
                else if (mesh.GetBlendShapeName(i).Contains("vrc.v_ou"))
                {
                    blendShapeMap["U"] = i;
                }
                else if (mesh.GetBlendShapeName(i).Contains("vrc.v_e"))
                {
                    blendShapeMap["E"] = i;
                }
                else if (mesh.GetBlendShapeName(i).Contains("vrc.v_oh"))
                {
                    blendShapeMap["O"] = i;
                }
            }

            return blendShapeMap;
        }
    }
}
