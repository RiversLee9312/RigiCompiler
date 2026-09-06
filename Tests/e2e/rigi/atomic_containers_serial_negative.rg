// expect-error: does not satisfy the 'With Serializable'
shared class Item {}
func bad(value:AtomicList\<Item>) {}
