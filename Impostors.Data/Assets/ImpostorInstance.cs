using System;
using UnityEngine;

namespace Impostors.Data
{
    [Serializable]
    public struct ImpostorInstance
    {
        public int Prototype;
        public Vector3 Position;
        public float Yaw;
        public float Scale;

        public readonly Matrix4x4 Matrix => Matrix4x4.TRS(Position, Quaternion.Euler(0, Yaw, 0), Vector3.one * Scale);
    }
}
