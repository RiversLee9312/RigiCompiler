/*
 * 标量 to_string 面族（MIDDLEWARE §4.8）：i64/u64/f64/f32/bool/char → UTF-8
 * 标准文本，以及 any_to_string（RUNTIME §26）。StringOut 出参置首参。
 * 文本口径对齐 BIL VM ToStandardText（CultureInfo.InvariantCulture /
 * bool true|false / char 码元）。
 *
 * f64/f32 已清偿：Ryu d2s/f2s 最短往返数字 + .NET 默认呈现层。
 * 科学记法当且仅当十进制指数 k < -4 或 k >= N（N 为 MaxRoundTripDigits：
 * f64=17、f32=9；.NET 10 ToString(InvariantCulture) 校准）。
 * 固定记法省略多余小数点与尾随零（4.0 → "4"）。
 * 科学记法形式 d[.ddd]E±XX（指数恒带符号、至少两位：1E+20、1E-05、1E+100）。
 * 特殊值：NaN → "NaN"；+Inf → "Infinity"；-Inf → "-Infinity"；-0.0 → "-0"。
 *
 * any_to_string：经槽指针读胖引用；内建标量走本面族（窄整数按符号性
 * widen）；tag1 且 TypeSheet 带 RIGI_TYPE_STRING → acquire 块内
 * {data,len} 后直接产出（不再 memcpy）；tag1 非 STRING（大 struct）
 * 走 TypeInfo.name（与 tag2 同，行为修正）。
 * tag2 不虚调 toString——Any/Object 默认体即调本面，虚调会无限递归
 *（与 VM hook 只走 ToStandardText、override 经方法虚派发不触达本面一致）。
 */
#include "arc.h"
#include "rigi_string.h"
#include "ryu.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* .NET 10 G 默认：nMaxDigits = max(DigitsCount, MaxRoundTripDigits)；
 * Ryu 有效位数不超过该上限，故阈值退化为 k >= 17 / 9 或 k < -4。 */
#define RIGI_DOTNET_SCI_LOW (-4)
#define RIGI_DOTNET_F64_SCI_GE 17
#define RIGI_DOTNET_F32_SCI_GE 9

static void rigi_set_string(rigi_string *out, const char *src, int64_t len)
{
    char *data = rigi_string_new(len);
    if (len > 0 && src != NULL)
    {
        memcpy(data, src, (size_t)len);
    }
    out->data = data;
    out->len = len;
}

static int rigi_u64_to_digits(uint64_t value, char *digits)
{
    char tmp[20];
    int n = 0;
    int i;
    if (value == 0)
    {
        digits[0] = '0';
        return 1;
    }
    while (value > 0)
    {
        tmp[n++] = (char)('0' + (value % 10));
        value /= 10;
    }
    for (i = 0; i < n; i++)
    {
        digits[i] = tmp[n - 1 - i];
    }
    return n;
}

/* 在 Ryu mantissa/exponent 上复刻 .NET 默认 G 呈现。sci_ge = 科学记法下限（k >= sci_ge）。 */
static int rigi_format_dotnet(char *buf, int negative, uint64_t mantissa, int32_t exponent, int sci_ge)
{
    char digits[20];
    int nd;
    int k;
    int i = 0;

    if (mantissa == 0)
    {
        if (negative)
        {
            buf[0] = '-';
            buf[1] = '0';
            return 2;
        }
        buf[0] = '0';
        return 1;
    }

    nd = rigi_u64_to_digits(mantissa, digits);
    k = nd + exponent - 1;
    if (negative)
    {
        buf[i++] = '-';
    }

    if (k < RIGI_DOTNET_SCI_LOW || k >= sci_ge)
    {
        int ae;
        buf[i++] = digits[0];
        if (nd > 1)
        {
            buf[i++] = '.';
            memcpy(buf + i, digits + 1, (size_t)(nd - 1));
            i += nd - 1;
        }
        buf[i++] = 'E';
        buf[i++] = k < 0 ? '-' : '+';
        ae = k < 0 ? -k : k;
        if (ae >= 100)
        {
            buf[i++] = (char)('0' + ae / 100);
            buf[i++] = (char)('0' + (ae / 10) % 10);
            buf[i++] = (char)('0' + ae % 10);
        }
        else
        {
            buf[i++] = (char)('0' + ae / 10);
            buf[i++] = (char)('0' + ae % 10);
        }
        return i;
    }

    if (k >= 0)
    {
        int int_digits = k + 1;
        if (nd <= int_digits)
        {
            memcpy(buf + i, digits, (size_t)nd);
            i += nd;
            while (nd < int_digits)
            {
                buf[i++] = '0';
                nd++;
            }
        }
        else
        {
            memcpy(buf + i, digits, (size_t)int_digits);
            i += int_digits;
            buf[i++] = '.';
            memcpy(buf + i, digits + int_digits, (size_t)(nd - int_digits));
            i += nd - int_digits;
        }
        return i;
    }

    {
        int zeros = -k - 1;
        buf[i++] = '0';
        buf[i++] = '.';
        while (zeros > 0)
        {
            buf[i++] = '0';
            zeros--;
        }
        memcpy(buf + i, digits, (size_t)nd);
        i += nd;
        return i;
    }
}

static void rigi_float_to_string(rigi_string *out, const rigi_ryu_dec *dec, int sci_ge)
{
    char buf[64];
    int n;
    if (dec->kind == RIGI_RYU_NAN)
    {
        rigi_set_string(out, "NaN", 3);
        return;
    }
    if (dec->kind == RIGI_RYU_INF)
    {
        if (dec->negative)
        {
            rigi_set_string(out, "-Infinity", 9);
        }
        else
        {
            rigi_set_string(out, "Infinity", 8);
        }
        return;
    }
    n = rigi_format_dotnet(buf, dec->negative, dec->mantissa, dec->exponent, sci_ge);
    rigi_set_string(out, buf, (int64_t)n);
}

void rigi_i64_to_string(rigi_string *out, int64_t value)
{
    char buf[32];
    int n = snprintf(buf, sizeof(buf), "%lld", (long long)value);
    if (n < 0 || n >= (int)sizeof(buf))
    {
        out->data = NULL;
        out->len = 0;
        return;
    }
    rigi_set_string(out, buf, (int64_t)n);
}

void rigi_u64_to_string(rigi_string *out, uint64_t value)
{
    char buf[32];
    int n = rigi_u64_to_digits(value, buf);
    rigi_set_string(out, buf, (int64_t)n);
}

void rigi_f64_to_string(rigi_string *out, double value)
{
    rigi_ryu_dec dec;
    rigi_ryu_d2d(value, &dec);
    rigi_float_to_string(out, &dec, RIGI_DOTNET_F64_SCI_GE);
}

void rigi_f32_to_string(rigi_string *out, float value)
{
    rigi_ryu_dec dec;
    rigi_ryu_f2d(value, &dec);
    rigi_float_to_string(out, &dec, RIGI_DOTNET_F32_SCI_GE);
}

void rigi_bool_to_string(rigi_string *out, int8_t value)
{
    if ((value & 1) != 0)
    {
        rigi_set_string(out, "true", 4);
    }
    else
    {
        rigi_set_string(out, "false", 5);
    }
}

/* char 是 UTF-16 码元（与 VM 的 C# char / LLVM i16 对齐）；BMP 内编 UTF-8 */
void rigi_char_to_string(rigi_string *out, int16_t value)
{
    char buf[3];
    int64_t len;
    uint16_t cu = (uint16_t)value;
    if (cu < 0x80u)
    {
        buf[0] = (char)cu;
        len = 1;
    }
    else if (cu < 0x800u)
    {
        buf[0] = (char)(0xC0u | (cu >> 6));
        buf[1] = (char)(0x80u | (cu & 0x3Fu));
        len = 2;
    }
    else
    {
        buf[0] = (char)(0xE0u | (cu >> 12));
        buf[1] = (char)(0x80u | ((cu >> 6) & 0x3Fu));
        buf[2] = (char)(0x80u | (cu & 0x3Fu));
        len = 3;
    }
    rigi_set_string(out, buf, len);
}

static int rigi_sheet_name_eq(const RigiTypeSheet *sheet, const char *want, int64_t want_len)
{
    const RigiTypeInfo *info;
    if (sheet == NULL || (info = sheet->typeInfoId) == NULL)
    {
        return 0;
    }
    if (info->name.len != want_len)
    {
        return 0;
    }
    if (want_len == 0)
    {
        return 1;
    }
    if (info->name.data == NULL)
    {
        return 0;
    }
    return memcmp(info->name.data, want, (size_t)want_len) == 0;
}

#define RIGI_SHEET_IS(sheet, lit) \
    rigi_sheet_name_eq((sheet), (lit), (int64_t)(sizeof(lit) - 1))

/* .typeid 构造族：TypeInfo.name 为 canonical core::Type<...>。
 * 不用 typeFlags 新位——arc.h 与发射无需同步；core:: 前缀用户类型不可达。 */
static int rigi_sheet_is_typeid_family(const RigiTypeSheet *sheet)
{
    const RigiTypeInfo *info;
    static const char prefix[] = "core::Type<";
    if (sheet == NULL || (info = sheet->typeInfoId) == NULL
        || info->name.data == NULL)
    {
        return 0;
    }
    if (info->name.len < (int64_t)(sizeof(prefix) - 1))
    {
        return 0;
    }
    return memcmp(info->name.data, prefix, sizeof(prefix) - 1) == 0;
}

static void rigi_copy_type_name(rigi_string *out, const RigiTypeSheet *sheet)
{
    const RigiTypeInfo *info;
    if (sheet == NULL || (info = sheet->typeInfoId) == NULL)
    {
        rigi_set_string(out, "", 0);
        return;
    }
    rigi_set_string(out, info->name.data, info->name.len);
}

/* TypeInfo.name 是 canonical；VM TypeId.ToStandardText = BIL 别名（.i32 等） */
static void rigi_copy_type_name_bil(rigi_string *out, const RigiTypeSheet *sheet)
{
    const RigiTypeInfo *info;
    if (sheet == NULL || (info = sheet->typeInfoId) == NULL)
    {
        rigi_set_string(out, "", 0);
        return;
    }
#define RIGI_BIL_ALIAS(canon, alias) \
    do { \
        if (info->name.len == (int64_t)(sizeof(canon) - 1) \
            && info->name.data != NULL \
            && memcmp(info->name.data, (canon), sizeof(canon) - 1) == 0) \
        { \
            rigi_set_string(out, (alias), (int64_t)(sizeof(alias) - 1)); \
            return; \
        } \
    } while (0)
    RIGI_BIL_ALIAS("core::i8", ".i8");
    RIGI_BIL_ALIAS("core::i16", ".i16");
    RIGI_BIL_ALIAS("core::i32", ".i32");
    RIGI_BIL_ALIAS("core::i64", ".i64");
    RIGI_BIL_ALIAS("core::u8", ".u8");
    RIGI_BIL_ALIAS("core::u16", ".u16");
    RIGI_BIL_ALIAS("core::u32", ".u32");
    RIGI_BIL_ALIAS("core::u64", ".u64");
    RIGI_BIL_ALIAS("core::float", ".f32");
    RIGI_BIL_ALIAS("core::double", ".f64");
    RIGI_BIL_ALIAS("core::bool", ".bool");
    RIGI_BIL_ALIAS("core::char", ".char");
    RIGI_BIL_ALIAS("core::String", ".string");
    RIGI_BIL_ALIAS("core::Any", ".any");
    RIGI_BIL_ALIAS("core::Object", ".object");
    RIGI_BIL_ALIAS("core::ValueType", ".valuetype");
#undef RIGI_BIL_ALIAS
    rigi_set_string(out, info->name.data, info->name.len);
}

static int rigi_scalar_to_string(rigi_string *out, const RigiTypeSheet *sheet,
    uint64_t payload)
{
    if (RIGI_SHEET_IS(sheet, "core::i64"))
    {
        rigi_i64_to_string(out, (int64_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::u64"))
    {
        rigi_u64_to_string(out, payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::i32"))
    {
        rigi_i64_to_string(out, (int64_t)(int32_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::u32"))
    {
        rigi_u64_to_string(out, (uint64_t)(uint32_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::i16"))
    {
        rigi_i64_to_string(out, (int64_t)(int16_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::u16"))
    {
        rigi_u64_to_string(out, (uint64_t)(uint16_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::i8"))
    {
        rigi_i64_to_string(out, (int64_t)(int8_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::u8"))
    {
        rigi_u64_to_string(out, (uint64_t)(uint8_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::double"))
    {
        double value;
        memcpy(&value, &payload, sizeof(value));
        rigi_f64_to_string(out, value);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::float"))
    {
        uint32_t bits = (uint32_t)payload;
        float value;
        memcpy(&value, &bits, sizeof(value));
        rigi_f32_to_string(out, value);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::bool"))
    {
        rigi_bool_to_string(out, (int8_t)payload);
        return 1;
    }
    if (RIGI_SHEET_IS(sheet, "core::char"))
    {
        rigi_char_to_string(out, (int16_t)payload);
        return 1;
    }
    return 0;
}

/* Any 经 16B 对齐槽指针（typeid + payload）；与生成代码 {i64,i64} 同布局。 */
void rigi_any_to_string(rigi_string *out, const void *anySlot)
{
    uint64_t type_id;
    uint64_t payload;
    uint64_t tag;
    const RigiTypeSheet *sheet;

    if (out == NULL)
    {
        return;
    }
    if (anySlot == NULL)
    {
        rigi_set_string(out, "", 0);
        return;
    }
    memcpy(&type_id, anySlot, sizeof(type_id));
    memcpy(&payload, (const char *)anySlot + 8, sizeof(payload));
    if (type_id == 0 && payload == 0)
    {
        rigi_set_string(out, "null", 4);
        return;
    }

    tag = type_id >> RIGI_TAG_SHIFT;
    if (tag == RIGI_TAG_OBJECT)
    {
        if (payload == 0)
        {
            rigi_set_string(out, "null", 4);
            return;
        }
        /* 实际类型取对象头 sheet（与 typecheck.c / VM TypeRef 同口径） */
        sheet = *(const RigiTypeSheet *const *)(void *)(uintptr_t)payload;
        rigi_copy_type_name(out, sheet);
        return;
    }

    sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
    if (rigi_scalar_to_string(out, sheet, payload))
    {
        return;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        /* 行为修正：STRING 堆块是 {data,len}，acquire 后直接产出，不再 memcpy；
         * tag1 非 STRING（大 struct）走 TypeInfo.name，与 tag2 同。 */
        if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_STRING) != 0)
        {
            if (payload != 0)
            {
                const rigi_string *block = (const rigi_string *)(uintptr_t)payload;
                rigi_string_acquire(block->data);
                out->data = block->data;
                out->len = block->len;
                return;
            }
            out->data = NULL;
            out->len = 0;
            return;
        }
        rigi_copy_type_name(out, sheet);
        return;
    }
    /* tag0 Type 构造族：payload = 所指 TypeSheet*，文本对齐 VM TypeSymbol */
    if (rigi_sheet_is_typeid_family(sheet))
    {
        rigi_copy_type_name_bil(out,
            (const RigiTypeSheet *)(uintptr_t)payload);
        return;
    }
    /* tag0 非标量（小 struct、enum 等）：类型 canonical 名 */
    rigi_copy_type_name(out, sheet);
}

/*
 * 对象身份原语（MW11d-D，交接 §14）：语言层无引用相等 ==（lambda 隐藏类
 * 无 operator equals），Receiver listener 身份键需要机制层身份通道。
 * Any 经 16B 对齐槽指针（type_id + payload），与 rigi_any_to_string 同
 * 布局；对象身份的进程内唯一标识 = payload（堆指针，对象存活期稳定且
 * 唯一——调用纪律要求被身份化的对象此刻仍被引用）。标量 payload 无身份
 * 语义（值装箱），本面不拒绝但调用方不应依赖。
 */
/* Place 身份比较只返回布尔值；两边的普通 ARC 引用保证目标仍存活。 */
uint8_t rigi_place_same_target(const void *left, const void *right)
{
    uint64_t leftPayload, rightPayload;
    if (left == NULL || right == NULL) return 0;
    memcpy(&leftPayload, (const char *)left + 8, sizeof(leftPayload));
    memcpy(&rightPayload, (const char *)right + 8, sizeof(rightPayload));
    return leftPayload == rightPayload;
}

/* Handle 固定 ABI：头 16B、隐藏胖 target 16B、可写能力位 16B。
 * target 是唯一 refMap 项；普通析构、循环扫描与 teardown 复用 ARC
 * 的既有扫描器，不能再添加第二条 release 路径。 */
static void *rigi_handle_object(const void *value)
{
    uint64_t type_id, payload;
    memcpy(&type_id, value, 8);
    memcpy(&payload, (const char *)value + 8, 8);
    const RigiTypeSheet *sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
    if ((type_id >> RIGI_TAG_SHIFT) != RIGI_TAG_OBJECT || payload == 0
        || sheet == NULL || sheet->typeInfoId == NULL
        || sheet->typeInfoId->name.len != 7
        || memcmp(sheet->typeInfoId->name.data, ".handle", 7) != 0) abort();
    return (void *)(uintptr_t)payload;
}

void rigi_handle_make(void *out, const RigiTypeSheet *sheet, const void *target, int32_t kind, uint8_t mutable)
{
    uint64_t target_type, target_payload;
    memcpy(&target_type, target, 8);
    memcpy(&target_payload, (const char *)target + 8, 8);
    if (sheet == NULL || sheet->typeSize != 48 || sheet->refMapSize != 1
        || sheet->refMap[0] != 0 || (sheet->typeFlags & RIGI_TYPE_SHARED) == 0
        || (target_type >> RIGI_TAG_SHIFT) != RIGI_TAG_OBJECT || target_payload == 0) abort();
    void *object = rigi_alloc(sheet);
    target_payload = rigi_ref_acquire(target_type, target_payload);
    memcpy((char *)object + 16, &target_type, 8);
    memcpy((char *)object + 24, &target_payload, 8);
    *((uint8_t *)object + 32) = mutable;
    memcpy((char *)object + 36, &kind, 4);
    uint64_t type_id = (uint64_t)(uintptr_t)sheet | ((uint64_t)RIGI_TAG_OBJECT << RIGI_TAG_SHIFT);
    uint64_t payload = (uint64_t)(uintptr_t)object;
    memcpy(out, &type_id, 8);
    memcpy((char *)out + 8, &payload, 8);
}

void rigi_handle_target(void *out, const void *value)
{
    const char *object = (const char *)rigi_handle_object(value);
    uint64_t type_id, payload;
    memcpy(&type_id, object + 16, 8);
    memcpy(&payload, object + 24, 8);
    payload = rigi_ref_acquire(type_id, payload);
    memcpy(out, &type_id, 8);
    memcpy((char *)out + 8, &payload, 8);
}

uint8_t rigi_handle_is_mutable(const void *value)
{
    return *((const uint8_t *)rigi_handle_object(value) + 32);
}

int32_t rigi_handle_kind(const void *value)
{
    int32_t kind;
    memcpy(&kind, (const char *)rigi_handle_object(value) + 36, 4);
    return kind;
}

uint8_t rigi_handle_type_is_value(const RigiTypeSheet *sheet)
{
    return sheet != NULL && (sheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0;
}
