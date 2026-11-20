#ifndef IMPOSTOR_VIEW_FRAME
#define IMPOSTOR_VIEW_FRAME

// The volume variant needs only two sun interpolators, leaving room for the
// three camera-frame bases. The fallback PCF variant keeps fragment-side bases
// because its per-sun projection rows already occupy TEXCOORD10-20.
#if defined(_IMPOSTOR_SELF_SHADOW_VOLUME)
struct ImpostorViewFrameVaryings
{
    nointerpolation float3 right0 : TEXCOORD10;
    nointerpolation float3 up0 : TEXCOORD11;
    nointerpolation float3 direction0 : TEXCOORD12;
    nointerpolation float3 right1 : TEXCOORD13;
    nointerpolation float3 up1 : TEXCOORD14;
    nointerpolation float3 direction1 : TEXCOORD15;
    nointerpolation float3 right2 : TEXCOORD16;
    nointerpolation float3 up2 : TEXCOORD17;
    nointerpolation float3 direction2 : TEXCOORD18;
};

void BuildViewFrameBasis(float frame, out float3 right, out float3 up, out float3 direction)
{
    direction = HemiDecode(float2(fmod(frame, _Frames), floor(frame / _Frames)) / (_Frames - 1));
    FrameBasis(direction, right, up);
}

ImpostorViewFrameVaryings BuildViewFrameVaryings(float3 frames)
{
    ImpostorViewFrameVaryings result = (ImpostorViewFrameVaryings)0;
    BuildViewFrameBasis(frames.x, result.right0, result.up0, result.direction0);
    BuildViewFrameBasis(frames.y, result.right1, result.up1, result.direction1);
    BuildViewFrameBasis(frames.z, result.right2, result.up2, result.direction2);
    return result;
}

void ReadViewFrameBasis(ImpostorViewFrameVaryings frames, int index,
    out float3 right, out float3 up, out float3 direction)
{
    if (index == 0)
    {
        right = frames.right0;
        up = frames.up0;
        direction = frames.direction0;
    }
    else if (index == 1)
    {
        right = frames.right1;
        up = frames.up1;
        direction = frames.direction1;
    }
    else
    {
        right = frames.right2;
        up = frames.up2;
        direction = frames.direction2;
    }
}
#endif

#endif
