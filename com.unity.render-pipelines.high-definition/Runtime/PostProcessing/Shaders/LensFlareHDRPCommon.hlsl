#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/Sampling.hlsl"

struct Attributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 texcoord : TEXCOORD0;
    float occlusion : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};

sampler2D _FlareTex;
TEXTURE2D_X(_FlareOcclusionBufferTex);

float4 _FlareColor;
float4 _FlareData0; // x: localCos0, y: localSin0, zw: PositionOffsetXY
float4 _FlareData1; // x: OcclusionRadius, y: OcclusionSampleCount, z: ScreenPosZ, w: Falloff
float4 _FlareData2; // xy: ScreenPos, zw: FlareSize
float4 _FlareData3; // xy: RayOffset

#define _LocalCos0          _FlareData0.x
#define _LocalSin0          _FlareData0.y
#define _PositionOffset     _FlareData0.zw

#define _ScreenPosZ         _FlareData1.z
#define _FlareFalloff       _FlareData1.w

#define _ScreenPos          _FlareData2.xy
#define _FlareSize          _FlareData2.zw

#define _FlareRayOffset     _FlareData3.xy
#define _FlareShapeInvSide  _FlareData3.z

float2 Rotate(float2 v, float cos0, float sin0)
{
    return float2(v.x * cos0 - v.y * sin0,
                  v.x * sin0 + v.y * cos0);
}

Varyings vert(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float screenRatio = _ScreenSize.y / _ScreenSize.x;

    float4 posPreScale = float4(2.0f, 2.0f, 1.0f, 1.0f) * GetQuadVertexPosition(input.vertexID) - float4(1.0f, 1.0f, 0.0f, 0.0);
    output.texcoord = GetQuadTexCoord(input.vertexID);

    posPreScale.xy *= _FlareSize;
    float2 local = Rotate(posPreScale.xy, _LocalCos0, _LocalSin0);

    local.x *= screenRatio;

    output.positionCS.xy = local + _ScreenPos + _FlareRayOffset + _PositionOffset;
    output.positionCS.zw = posPreScale.zw;

    output.occlusion = 1.0f;

    return output;
}

float4 ComputeGlow(float2 uv)
{
    float2 v = (uv - 0.5f) * 2.0f;

    float sdf = saturate(-(length(v) - 1.0f));

    return pow(sdf, _FlareFalloff);
}

float4 ComputeIris(float2 uv_)
{
    float2 uv = (uv_ - 0.5f) * 2.0f;

    float a = atan2(uv.x, uv.y) + 3.1415926535897932384626433832795f;
    float rx = (2.0f * 3.1415926535897932384626433832795f) * _FlareShapeInvSide;
    float d = 2.0f * cos(floor(0.5f + a / rx) * rx - a) * length(uv);

    //return pow(saturate(1.0f - d), _FlareFalloff);
    return saturate(1.0f - d) * pow(saturate(1.0f - d), _FlareFalloff);
}

float4 GetFlareColor(float2 uv)
{
#if FLARE_GLOW
    return ComputeGlow(uv);
#elif FLARE_IRIS
    return ComputeIris(uv);
#else
    return tex2D(_FlareTex, uv);
#endif
}
