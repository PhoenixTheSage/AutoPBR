// Genesis desktop material/draw metadata table. GLES/ANGLE keeps the scalar uniform path.
#ifndef GENESIS_DRAW_RECORD_GLSL
#define GENESIS_DRAW_RECORD_GLSL

#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
struct GenesisMaterialDrawRecord
{
    // x: parallax UV scale, y/z: texture atlas scale, w: height texture width
    vec4 params0;
    // x: height texture height, y: material texture-array layer, zw: reserved
    vec4 params1;
    // x: parallax, y: parallax AO, z: parallax shadow, w: tessellation displacement
    vec4 flags0;
    // x: has normal, y: has specular, z: has height, w: entity alpha mode
    vec4 flags1;
};

layout(std430, binding = 8) readonly buffer GenesisMaterialDrawRecords
{
    GenesisMaterialDrawRecord uGenesisMaterialDrawRecords[];
};

uniform int uGenesisUseMaterialDrawRecord;
uniform int uGenesisDrawRecordIndex;

#ifdef GENESIS_DRAW_RECORD_BASE_INSTANCE
#ifdef GENESIS_VERTEX_STAGE
flat out int vGenesisDrawRecordIndex;

int genesisDrawRecordIndexValue()
{
    return int(gl_BaseInstanceARB);
}

void genesisWriteDrawRecordIndexVarying()
{
    vGenesisDrawRecordIndex = genesisDrawRecordIndexValue();
}
#elif defined(GENESIS_TESS_CONTROL_STAGE)
flat in int vGenesisDrawRecordIndex[];
flat out int tcGenesisDrawRecordIndex[];

int genesisDrawRecordIndexValue()
{
    return vGenesisDrawRecordIndex[0];
}

void genesisWriteDrawRecordIndexVarying()
{
    tcGenesisDrawRecordIndex[gl_InvocationID] = vGenesisDrawRecordIndex[gl_InvocationID];
}
#elif defined(GENESIS_TESS_EVALUATION_STAGE)
flat in int tcGenesisDrawRecordIndex[];
flat out int vGenesisDrawRecordIndex;

int genesisDrawRecordIndexValue()
{
    return tcGenesisDrawRecordIndex[0];
}

void genesisWriteDrawRecordIndexVarying()
{
    vGenesisDrawRecordIndex = genesisDrawRecordIndexValue();
}
#elif defined(GENESIS_FRAGMENT_STAGE)
flat in int vGenesisDrawRecordIndex;

int genesisDrawRecordIndexValue()
{
    return vGenesisDrawRecordIndex;
}

void genesisWriteDrawRecordIndexVarying()
{
}
#else
int genesisDrawRecordIndexValue()
{
    return uGenesisDrawRecordIndex;
}

void genesisWriteDrawRecordIndexVarying()
{
}
#endif
#else
int genesisDrawRecordIndexValue()
{
    return uGenesisDrawRecordIndex;
}

void genesisWriteDrawRecordIndexVarying()
{
}
#endif

GenesisMaterialDrawRecord genesisMaterialDrawRecord()
{
    return uGenesisMaterialDrawRecords[max(genesisDrawRecordIndexValue(), 0)];
}

int genesisFlag(float value)
{
    return int(floor(value + 0.5));
}
#else
// Vertex/tess stages call this unconditionally; keep a no-op when the SSBO table is unavailable (GLES/ANGLE).
void genesisWriteDrawRecordIndexVarying()
{
}
#endif

bool genesisUsesMaterialDrawRecord()
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    return uGenesisUseMaterialDrawRecord > 0;
#else
    return false;
#endif
}

vec2 genesisTextureAtlasScale(vec2 fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        GenesisMaterialDrawRecord record = genesisMaterialDrawRecord();
        return record.params0.yz;
    }
#endif
    return fallbackValue;
}

float genesisParallaxUvScale(float fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        GenesisMaterialDrawRecord record = genesisMaterialDrawRecord();
        return record.params0.x;
    }
#endif
    return fallbackValue;
}

vec2 genesisParallaxHeightTexSize(vec2 fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        GenesisMaterialDrawRecord record = genesisMaterialDrawRecord();
        return vec2(record.params0.w, record.params1.x);
    }
#endif
    return fallbackValue;
}

int genesisEnableParallax(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags0.x);
    }
#endif
    return fallbackValue;
}

int genesisEnableParallaxAo(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags0.y);
    }
#endif
    return fallbackValue;
}

int genesisEnableParallaxShadow(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags0.z);
    }
#endif
    return fallbackValue;
}

int genesisEnableTessellationDisplacement(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags0.w);
    }
#endif
    return fallbackValue;
}

int genesisHasNormal(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags1.x);
    }
#endif
    return fallbackValue;
}

int genesisHasSpecular(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags1.y);
    }
#endif
    return fallbackValue;
}

int genesisHasHeight(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags1.z);
    }
#endif
    return fallbackValue;
}

int genesisEntityAlphaMode(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().flags1.w);
    }
#endif
    return fallbackValue;
}

int genesisMaterialTextureLayer(int fallbackValue)
{
#ifdef GENESIS_MATERIAL_DRAW_RECORD_SSBO
    if (genesisUsesMaterialDrawRecord())
    {
        return genesisFlag(genesisMaterialDrawRecord().params1.y);
    }
#endif
    return fallbackValue;
}

#endif
