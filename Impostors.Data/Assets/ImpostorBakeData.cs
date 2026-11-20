using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Impostors.Data
{
    [Serializable]
    [MovedFrom(true, sourceNamespace: "Impostors", sourceAssembly: "Impostors.Runtime")]
    public sealed class ImpostorBakeData
    {
        public ScriptableObject Settings;
        public string SettingsJson;
        public string InputFingerprint;

        [HideInInspector]
        public UnityEngine.Object[] Artifacts = Array.Empty<UnityEngine.Object>();
    }
}
