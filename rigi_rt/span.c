/*
 * Span / SharedSpan 分配面（MW7b）：与数组同构，区别仅在 sheet 身份
 * （core::Span / core::SharedSpan）。析构复用 RIGI_TYPE_ARRAY 走查，
 * 不另开析构路径。
 */
#include "arc.h"

void *rigi_span_alloc(const RigiTypeSheet *spanSheet, const RigiTypeSheet *elemSheet,
    int32_t len)
{
    /* 与数组同构，区别仅在 sheet 身份（core::Span / core::SharedSpan） */
    return rigi_alloc_contiguous(spanSheet, elemSheet, len);
}
