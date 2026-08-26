/*
 * rigi_rt 类型检查 helper（MW5 c3）：is / supers / with。
 * 胖引用以 typeid + payload 两枚 i64 传入（与生成代码声明同形，双平台
 * 整数 ABI 无分歧）；tag2 取对象头实际 TypeSheet，tag0/tag1 掩码 typeid。
 */
#include "arc.h"

#include <stddef.h>
#include <stdint.h>

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
    return rigi_sheet_is(rigi_actual_sheet(type_id, payload), target);
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
