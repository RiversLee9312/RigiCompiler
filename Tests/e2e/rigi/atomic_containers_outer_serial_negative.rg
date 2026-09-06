// 外层型参只有共享界还不够，Serializable 证明也不可省略。
// expect-error: does not satisfy the 'With Serializable'
shared open class SharedBase {}
func bad\<T extends SharedBase>(value:AtomicList\<T>) {}
