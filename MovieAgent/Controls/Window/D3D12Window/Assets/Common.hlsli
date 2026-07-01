struct VSInput {
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD;
};
struct PSInput {
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD;
};