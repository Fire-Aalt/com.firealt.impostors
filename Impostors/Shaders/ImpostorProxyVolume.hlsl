#ifndef IMPOSTOR_PROXY_VOLUME
#define IMPOSTOR_PROXY_VOLUME

// The source 14-DOP is seven pairs of parallel planes. Keep its volume while
// rasterizing only a camera-facing quad, avoiding tiny proxy-face triangles.
float4 ProxyDiagonals(float3 value)
{
    return float4(value.x + value.y + value.z, value.x + value.y - value.z,
        value.x - value.y + value.z, -value.x + value.y + value.z);
}

float4 ProxySafeDenominator(float4 value)
{
    return float4(value.x < 0 ? -1 : 1, value.y < 0 ? -1 : 1,
        value.z < 0 ? -1 : 1, value.w < 0 ? -1 : 1) * max(abs(value), 1e-8);
}

float2 IntersectProxyVolume(float3 origin, float3 direction)
{
    float3 inverseAxis = rcp(ProxySafeDenominator(float4(direction, 1)).xyz);
    float3 first = (_ProxyBoundsMin.xyz - origin) * inverseAxis;
    float3 second = (_ProxyBoundsMax.xyz - origin) * inverseAxis;
    float3 nearAxis = min(first, second), farAxis = max(first, second);
    float4 inverseDiagonal = rcp(ProxySafeDenominator(ProxyDiagonals(direction)));
    float4 diagonalOrigin = ProxyDiagonals(origin);
    float4 diagonalFirst = (_ProxyDiagonalMin - diagonalOrigin) * inverseDiagonal;
    float4 diagonalSecond = (_ProxyDiagonalMax - diagonalOrigin) * inverseDiagonal;
    float4 nearDiagonal = min(diagonalFirst, diagonalSecond), farDiagonal = max(diagonalFirst, diagonalSecond);
    bool3 parallelAxis = abs(direction) < 1e-8;
    bool4 parallelDiagonal = abs(ProxyDiagonals(direction)) < 1e-8;
    if (any(parallelAxis && (origin < _ProxyBoundsMin.xyz || origin > _ProxyBoundsMax.xyz))
        || any(parallelDiagonal && (diagonalOrigin < _ProxyDiagonalMin || diagonalOrigin > _ProxyDiagonalMax)))
    {
        return float2(1, -1);
    }
    nearAxis = lerp(nearAxis, -1e20, float3(parallelAxis));
    farAxis = lerp(farAxis, 1e20, float3(parallelAxis));
    nearDiagonal = lerp(nearDiagonal, -1e20, float4(parallelDiagonal));
    farDiagonal = lerp(farDiagonal, 1e20, float4(parallelDiagonal));
    float entry = max(max(nearAxis.x, max(nearAxis.y, nearAxis.z)),
        max(max(nearDiagonal.x, nearDiagonal.y), max(nearDiagonal.z, nearDiagonal.w)));
    float exit = min(min(farAxis.x, min(farAxis.y, farAxis.z)),
        min(min(farDiagonal.x, farDiagonal.y), min(farDiagonal.z, farDiagonal.w)));
    return float2(entry, exit);
}

float3 CameraProxyPosition(float2 corner, float3 direction)
{
    float3 right, up;
    FrameBasis(direction, right, up);
    float3 extent = (_ProxyBoundsMax.xyz - _ProxyBoundsMin.xyz) * 0.5;
    float3 center = (_ProxyBoundsMax.xyz + _ProxyBoundsMin.xyz) * 0.5;
    return center + direction * dot(abs(direction), extent)
        + right * (corner.x * dot(abs(right), extent))
        + up * (corner.y * dot(abs(up), extent));
}
#endif
