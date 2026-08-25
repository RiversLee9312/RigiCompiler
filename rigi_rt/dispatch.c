/*
 * rigi_rt 派发 helper 面（MW4 批 2）：虚派发/接口派发的 C 侧查表实现。
 * 对象头 [0] 即实际类型的 TypeSheet 裸指针（rigi_alloc 写入，无 tag；
 * 胖引用 typeid 段的高字节 tag 只在引用层，不进对象头）。
 */
#include "arc.h"

#include <stdio.h>
#include <stdlib.h>

/* 虚派发：实际 TypeSheet → vTable[slot] */
void *rigi_vtable_entry(void *object, uint32_t slot)
{
    const RigiTypeSheet *sheet = *(RigiTypeSheet *const *)object;
    return sheet->vTable[slot];
}

/* 接口派发（RUNTIME §8）：沿 baseTypeId 链线性查 iMap 得段基址（条目
 * 少；加载期按地址排序的语义 = 发射期已排序，等价），加接口内槽序。
 * 未命中属模块形态错误（编译器 bug）：响亮失败 */
void *rigi_imap_entry(void *object, const RigiTypeSheet *iface, uint32_t slot)
{
    const RigiTypeSheet *sheet = *(RigiTypeSheet *const *)object;
    const RigiTypeSheet *type;
    for (type = sheet; type != NULL; type = type->baseTypeId)
    {
        const RigiImapPair *pairs = (const RigiImapPair *)type->iMap;
        uint32_t i;
        for (i = 0; i < type->iMapSize; i++)
        {
            if (pairs[i].iface == iface)
            {
                return sheet->vTable[pairs[i].base + slot];
            }
        }
    }
    fprintf(stderr, "rigi_rt: imap lookup failed\n");
    abort();
}
