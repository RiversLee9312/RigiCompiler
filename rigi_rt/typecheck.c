/*
 * rigi_rt 类型检查 helper（MW5 c3）：is / supers / with。
 * 胖引用以 typeid + payload 两枚 i64 传入（与生成代码声明同形，双平台
 * 整数 ABI 无分歧）；tag2 取对象头实际 TypeSheet，tag0/tag1 掩码 typeid。
 */
#include "arc.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

static const RigiTypeSheet *rigi_actual_sheet(uint64_t type_id, uint64_t payload)
{
    if (type_id == 0 && payload == 0)
    {
        return NULL;
    }
    if ((type_id >> RIGI_TAG_SHIFT) == RIGI_TAG_OBJECT)
    {
        if (payload == 0)
        {
            return NULL;
        }
        return *(const RigiTypeSheet *const *)(void *)(uintptr_t)payload;
    }
    return (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
}

static int32_t rigi_info_has_iface(const RigiTypeInfo *info,
    const RigiTypeSheet *iface)
{
    int32_t i;
    if (info == NULL || info->ifaceClosure == NULL)
    {
        return 0;
    }
    for (i = 0; i < info->ifaceClosureCount; i++)
    {
        if (info->ifaceClosure[i] == iface)
        {
            return 1;
        }
    }
    return 0;
}

static int32_t rigi_sheet_is(const RigiTypeSheet *actual, const RigiTypeSheet *target)
{
    const RigiTypeSheet *type;
    if (actual == NULL || target == NULL)
    {
        return 0;
    }
    /* 内建基元/默认值类型不携带显式基类 sheet 链，ValueType 根由
     * 统一布局位判定；与 VM 的值类型分类一致。 */
    if (target->typeInfoId != NULL && target->typeInfoId->name.data != NULL
        && target->typeInfoId->name.len == 15
        && memcmp(target->typeInfoId->name.data, "core::ValueType", 15) == 0)
        return (actual->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0;
    /* Any/Object 根规则（review-20260910 #14）：一切非 null 值的实际类型
     * 都是 Any/Object 的子孙——Any 不在 sheet 的 baseTypeId/ifaceClosure
     * 链上，不缺这条根规则时「开放泛型 Nullable 拆包且具化为 Any」
     * （如 MapEnumerator<String, Any>.current 的 `as V`）会误判失败。
     * VM 侧 TypesAssignable 同口径根规则（review-20260910 #01）。 */
    if (target->typeInfoId != NULL && target->typeInfoId->name.data != NULL
        && ((target->typeInfoId->name.len == 9
            && memcmp(target->typeInfoId->name.data, "core::Any", 9) == 0)
            || (target->typeInfoId->name.len == 12
                && memcmp(target->typeInfoId->name.data, "core::Object", 12) == 0)))
        return 1;
    for (type = actual; type != NULL; type = type->baseTypeId)
    {
        if (type == target || rigi_info_has_iface(type->typeInfoId, target))
        {
            return 1;
        }
    }
    return 0;
}

int32_t rigi_type_is(uint64_t type_id, uint64_t payload, const RigiTypeSheet *target)
{
    if (target != NULL && target->typeInfoId != NULL
        && target->typeInfoId->typeIdBound != NULL)
    {
        const RigiTypeSheet *actual = rigi_actual_sheet(type_id, payload);
        const RigiTypeSheet *bound = target->typeInfoId->typeIdBound;
        /* 先验证源是类型句柄，再将 payload 当 sheet；tag0 本身不足以
         * 证明指针来源（整数、浮点等也是 tag0）。 */
        if ((type_id >> RIGI_TAG_SHIFT) != RIGI_TAG_INLINE || payload == 0
            || actual == NULL || actual->typeInfoId == NULL
            || actual->typeInfoId->typeIdBound == NULL) return 0;
        if (bound->typeInfoId != NULL && bound->typeInfoId->name.data != NULL
            && bound->typeInfoId->name.len == 9
            && memcmp(bound->typeInfoId->name.data, "core::Any", 9) == 0) return 1;
        return rigi_sheet_is((const RigiTypeSheet *)(uintptr_t)payload, bound);
    }
    if (target != NULL && target->typeInfoId != NULL
        && target->typeInfoId->nullableElement != NULL)
        return rigi_type_is(type_id, payload, target->typeInfoId->nullableElement);
    return rigi_sheet_is(rigi_actual_sheet(type_id, payload), target);
}

/* typeOf 值形态：返回实际 TypeSheet*。null 胖引用仍返 NULL，
 * 生成代码替换为 @typesheet..null（C 看不到内部链接全局）。 */
const RigiTypeSheet *rigi_typeof(uint64_t type_id, uint64_t payload)
{
    return rigi_actual_sheet(type_id, payload);
}

int32_t rigi_type_is_indirect(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *target)
{
    return rigi_type_is(type_id, payload, target);
}

int32_t rigi_type_supers(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *target)
{
    const RigiTypeSheet *actual;
    const RigiTypeSheet *type;
    actual = rigi_actual_sheet(type_id, payload);
    if (actual == NULL || target == NULL)
    {
        return 0;
    }
    /* 逆变：沿 target.baseTypeId 找 actual（类继承）；接口目标另查
     * TypeInfo.ifaceClosure（传递 implements 闭包，含多父）。 */
    for (type = target; type != NULL; type = type->baseTypeId)
    {
        if (type == actual)
        {
            return 1;
        }
    }
    if (rigi_info_has_iface(target->typeInfoId, actual))
    {
        return 1;
    }
    return 0;
}

int32_t rigi_type_supers_indirect(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *target)
{
    return rigi_type_supers(type_id, payload, target);
}

static int32_t rigi_info_has_wrapper(const RigiTypeInfo *info,
    const RigiTypeSheet *wrapper)
{
    int32_t i;
    if (info == NULL || info->wrappers == NULL)
    {
        return 0;
    }
    for (i = 0; i < info->wrapperCount; i++)
    {
        if (info->wrappers[i] == wrapper)
        {
            return 1;
        }
    }
    return 0;
}

static int32_t rigi_sheet_has_wrapper(const RigiTypeSheet *type,
    const RigiTypeSheet *wrapper)
{
    const RigiImapPair *pairs;
    uint32_t i;
    const RigiTypeSheet *cur;
    if (type == NULL || wrapper == NULL)
    {
        return 0;
    }
    for (cur = type; cur != NULL; cur = cur->baseTypeId)
    {
        if (rigi_info_has_wrapper(cur->typeInfoId, wrapper))
        {
            return 1;
        }
        if (cur->iMap == NULL)
        {
            continue;
        }
        pairs = (const RigiImapPair *)cur->iMap;
        for (i = 0; i < cur->iMapSize; i++)
        {
            const RigiTypeSheet *iface;
            for (iface = pairs[i].iface; iface != NULL; iface = iface->baseTypeId)
            {
                if (rigi_info_has_wrapper(iface->typeInfoId, wrapper))
                {
                    return 1;
                }
            }
        }
    }
    return 0;
}

int32_t rigi_type_with(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *wrapper)
{
    return rigi_sheet_has_wrapper(rigi_actual_sheet(type_id, payload), wrapper);
}

int32_t rigi_type_with_indirect(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *wrapper)
{
    return rigi_type_with(type_id, payload, wrapper);
}

/* ===== MW8c-2 动态 cast / 浮点→整数 ===== */

static int rigi_cast_sheet_name_eq(const RigiTypeSheet *sheet, const char *want,
    int64_t want_len)
{
    const RigiTypeInfo *info;
    if (sheet == NULL || (info = sheet->typeInfoId) == NULL)
    {
        return 0;
    }
    if (info->name.len != want_len || info->name.data == NULL)
    {
        return 0;
    }
    return memcmp(info->name.data, want, (size_t)want_len) == 0;
}

#define RIGI_CAST_SHEET_IS(sheet, lit) \
    rigi_cast_sheet_name_eq((sheet), (lit), (int64_t)(sizeof(lit) - 1))

enum
{
    RIGI_NK_NONE = 0,
    RIGI_NK_I8,
    RIGI_NK_U8,
    RIGI_NK_I16,
    RIGI_NK_U16,
    RIGI_NK_I32,
    RIGI_NK_U32,
    RIGI_NK_I64,
    RIGI_NK_U64,
    RIGI_NK_F32,
    RIGI_NK_F64,
    RIGI_NK_CHAR
};

static int rigi_numeric_kind(const RigiTypeSheet *sheet)
{
    if (RIGI_CAST_SHEET_IS(sheet, "core::i8")) return RIGI_NK_I8;
    if (RIGI_CAST_SHEET_IS(sheet, "core::u8")) return RIGI_NK_U8;
    if (RIGI_CAST_SHEET_IS(sheet, "core::i16")) return RIGI_NK_I16;
    if (RIGI_CAST_SHEET_IS(sheet, "core::u16")) return RIGI_NK_U16;
    if (RIGI_CAST_SHEET_IS(sheet, "core::i32")) return RIGI_NK_I32;
    if (RIGI_CAST_SHEET_IS(sheet, "core::u32")) return RIGI_NK_U32;
    if (RIGI_CAST_SHEET_IS(sheet, "core::i64")) return RIGI_NK_I64;
    if (RIGI_CAST_SHEET_IS(sheet, "core::u64")) return RIGI_NK_U64;
    if (RIGI_CAST_SHEET_IS(sheet, "core::float")) return RIGI_NK_F32;
    if (RIGI_CAST_SHEET_IS(sheet, "core::double")) return RIGI_NK_F64;
    if (RIGI_CAST_SHEET_IS(sheet, "core::char")) return RIGI_NK_CHAR;
    return RIGI_NK_NONE;
}

int64_t rigi_cast_f64_to_int(double v, int32_t kind)
{
    /* kind 0=i32 饱和（窄整数再截断）1=u32 饱和 2=i64 饱和 3=u64 饱和。
     * 对齐 .NET 10 unchecked (T)double：NaN→0，溢出饱和。 */
    if (v != v)
    {
        return 0;
    }
    if (kind == 1)
    {
        if (v >= 4294967295.0) return (int64_t)(uint32_t)4294967295u;
        if (v <= 0.0) return 0;
        return (int64_t)(uint32_t)v;
    }
    if (kind == 2)
    {
        if (v >= 9223372036854775808.0) return (int64_t)9223372036854775807LL;
        if (v < -9223372036854775808.0) return (int64_t)((uint64_t)1 << 63);
        return (int64_t)v;
    }
    if (kind == 3)
    {
        if (v <= 0.0) return 0;
        if (v >= 18446744073709551616.0) return (int64_t)(~(uint64_t)0);
        return (int64_t)(uint64_t)v;
    }
    if (v >= 2147483647.0) return 2147483647;
    if (v <= -2147483648.0) return (int64_t)(-2147483647 - 1);
    return (int64_t)(int32_t)v;
}

static uint64_t rigi_pack_int_payload(int kind, int64_t signed_v, uint64_t unsigned_v,
    int src_signed)
{
    int64_t v = src_signed ? signed_v : (int64_t)unsigned_v;
    uint64_t u = src_signed ? (uint64_t)signed_v : unsigned_v;
    switch (kind)
    {
    case RIGI_NK_I8: return (uint64_t)(uint8_t)(int8_t)v;
    case RIGI_NK_U8: return (uint64_t)(uint8_t)u;
    case RIGI_NK_I16: return (uint64_t)(uint16_t)(int16_t)v;
    case RIGI_NK_U16:
    case RIGI_NK_CHAR: return (uint64_t)(uint16_t)u;
    case RIGI_NK_I32: return (uint64_t)(uint32_t)(int32_t)v;
    case RIGI_NK_U32: return (uint64_t)(uint32_t)u;
    case RIGI_NK_I64: return (uint64_t)v;
    case RIGI_NK_U64: return u;
    default: return 0;
    }
}

static int rigi_numeric_convert(int src_kind, uint64_t src_payload, int dst_kind,
    uint64_t *out_payload)
{
    double as_f64;
    int src_signed;
    int64_t s;
    uint64_t u;
    float f32;
    uint32_t fbits;
    if (src_kind == RIGI_NK_NONE || dst_kind == RIGI_NK_NONE)
    {
        return 0;
    }
    if (src_kind == RIGI_NK_F32)
    {
        fbits = (uint32_t)src_payload;
        memcpy(&f32, &fbits, sizeof(f32));
        as_f64 = (double)f32;
        goto from_float;
    }
    if (src_kind == RIGI_NK_F64)
    {
        memcpy(&as_f64, &src_payload, sizeof(as_f64));
        goto from_float;
    }
    src_signed = src_kind == RIGI_NK_I8 || src_kind == RIGI_NK_I16
        || src_kind == RIGI_NK_I32 || src_kind == RIGI_NK_I64;
    switch (src_kind)
    {
    case RIGI_NK_I8: s = (int8_t)src_payload; u = (uint64_t)s; break;
    case RIGI_NK_U8: u = (uint8_t)src_payload; s = (int64_t)u; break;
    case RIGI_NK_I16: s = (int16_t)src_payload; u = (uint64_t)s; break;
    case RIGI_NK_U16:
    case RIGI_NK_CHAR: u = (uint16_t)src_payload; s = (int64_t)u; break;
    case RIGI_NK_I32: s = (int32_t)src_payload; u = (uint64_t)s; break;
    case RIGI_NK_U32: u = (uint32_t)src_payload; s = (int64_t)u; break;
    case RIGI_NK_I64: s = (int64_t)src_payload; u = (uint64_t)s; break;
    default: u = src_payload; s = (int64_t)u; break;
    }
    if (dst_kind == RIGI_NK_F32 || dst_kind == RIGI_NK_F64)
    {
        as_f64 = src_signed ? (double)s : (double)u;
        if (dst_kind == RIGI_NK_F32)
        {
            f32 = (float)as_f64;
            memcpy(&fbits, &f32, sizeof(f32));
            *out_payload = (uint64_t)fbits;
        }
        else
        {
            memcpy(out_payload, &as_f64, sizeof(as_f64));
        }
        return 1;
    }
    *out_payload = rigi_pack_int_payload(dst_kind, s, u, src_signed);
    return 1;

from_float:
    if (dst_kind == RIGI_NK_F32)
    {
        f32 = (float)as_f64;
        memcpy(&fbits, &f32, sizeof(f32));
        *out_payload = (uint64_t)fbits;
        return 1;
    }
    if (dst_kind == RIGI_NK_F64)
    {
        memcpy(out_payload, &as_f64, sizeof(as_f64));
        return 1;
    }
    {
        int32_t kind = 0;
        int64_t bits;
        if (dst_kind == RIGI_NK_U32) kind = 1;
        else if (dst_kind == RIGI_NK_I64) kind = 2;
        else if (dst_kind == RIGI_NK_U64) kind = 3;
        bits = rigi_cast_f64_to_int(as_f64, kind);
        /* kind0：i32 饱和后再按位截断（u8(-1.5)=255）；其余已是目标位宽 */
        if (kind == 0)
        {
            *out_payload = rigi_pack_int_payload(dst_kind, bits, (uint64_t)bits, 1);
        }
        else
        {
            *out_payload = (uint64_t)bits;
        }
        return 1;
    }
}

static void rigi_rewrite_view(uint64_t src_tid, uint64_t src_pl,
    const RigiTypeSheet *target, uint64_t *out_tid, uint64_t *out_pl)
{
    uint64_t tag = src_tid >> RIGI_TAG_SHIFT;
    /* 值没有对象头保存实际类型；Any/接口视图必须保留原 sheet，
     * 否则后续派发与 ARC 会误把接口空壳当作值布局。 */
    if (tag != RIGI_TAG_OBJECT && target != NULL
        && (target->typeFlags & RIGI_TYPE_INLINE_VALUE) == 0)
    {
        *out_tid = src_tid;
        *out_pl = src_pl;
        return;
    }
    *out_tid = ((uint64_t)(uintptr_t)target & RIGI_SHEET_MASK)
        | (tag << RIGI_TAG_SHIFT);
    *out_pl = src_pl;
}

int32_t rigi_try_cast(uint64_t src_type_id, uint64_t src_payload,
    const RigiTypeSheet *target, uint64_t *out_type_id, uint64_t *out_payload)
{
    const RigiTypeSheet *actual;
    int src_kind;
    int dst_kind;
    uint64_t converted;
    if (out_type_id == NULL || out_payload == NULL)
    {
        return 0;
    }
    /* Nullable<T> 只接纳 null 或可转换为元素 T 的值，保留原胖值表示。 */
    if (target != NULL && target->typeInfoId != NULL
        && target->typeInfoId->nullableElement != NULL)
    {
        if (src_type_id == 0 && src_payload == 0)
        {
            *out_type_id = 0;
            *out_payload = 0;
            return 1;
        }
        return rigi_try_cast(src_type_id, src_payload,
            target->typeInfoId->nullableElement, out_type_id, out_payload);
    }
    /* null 胖引用：引用目标放行（零胖引用），值类型失败 */
    if (src_type_id == 0 && src_payload == 0)
    {
        if (target != NULL && (target->typeFlags & RIGI_TYPE_INLINE_VALUE) == 0)
        {
            *out_type_id = 0;
            *out_payload = 0;
            return 1;
        }
        return 0;
    }
    actual = rigi_actual_sheet(src_type_id, src_payload);
    if (target != NULL && target->typeInfoId != NULL
        && target->typeInfoId->typeIdBound != NULL)
    {
        if (!rigi_type_is(src_type_id, src_payload, target)) return 0;
        *out_type_id = src_type_id;
        *out_payload = src_payload;
        return 1;
    }
    if (rigi_sheet_is(actual, target))
    {
        rigi_rewrite_view(src_type_id, src_payload, target, out_type_id, out_payload);
        return 1;
    }
    src_kind = rigi_numeric_kind(actual);
    dst_kind = rigi_numeric_kind(target);
    if (rigi_numeric_convert(src_kind, src_payload, dst_kind, &converted))
    {
        *out_type_id = (uint64_t)(uintptr_t)target & RIGI_SHEET_MASK;
        *out_payload = converted;
        return 1;
    }
    /* Any/Object 目标：保持原胖引用（实际 typeid 仍在 tag/sheet） */
    if (RIGI_CAST_SHEET_IS(target, "core::Any")
        || RIGI_CAST_SHEET_IS(target, "core::Object"))
    {
        *out_type_id = src_type_id;
        *out_payload = src_payload;
        return 1;
    }
    return 0;
}
