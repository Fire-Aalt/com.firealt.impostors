namespace Impostors.Data
{
    public static class ImpostorShaderPasses
    {
        public const string DEBUG_CAPTURE = "DebugCapture";
        public const string FORWARD_LIT = "ForwardLit";
        public const string FORWARD_LIGHT_MODE = "UniversalForwardOnly";
        public const string OFFSCREEN_LIGHT_MODE = "ImpostorForwardOffscreen";
        public const string PREPASS_DEPTH_LIGHT_MODE = "ImpostorDepthOnly";
        public const string PREPASS_DEPTH_NORMALS_LIGHT_MODE = "ImpostorDepthNormals";
    }
}
