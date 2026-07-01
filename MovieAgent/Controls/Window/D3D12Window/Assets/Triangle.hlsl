struct VSInput {
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD;
};
struct PSInput {
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD;
};

PSInput VSMain(VSInput input) {
    PSInput o;
    o.Position = float4(input.Position, 0, 1);
    o.TexCoord = input.TexCoord;
    return o;
}

Texture2D tex : register(t0);
SamplerState samp : register(s0);

float4 PSMain(PSInput input) : SV_TARGET {
    return tex.Sample(samp, input.TexCoord);
}