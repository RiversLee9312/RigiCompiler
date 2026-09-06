namespace core

// Map 快照分别存储键和值，Pair 仅由本地枚举器临时构造，不伪造其共享能力。
pub shared class AtomicMapSnapshot\<K with core.serialization.Serializable, V with core.serialization.Serializable> implements core.collections.IEnumerable\<Pair\<K, V>> {
    priv const keys: Array\<K>
    priv const values: Array\<V>

    pub init(sourceKeys: Array\<K>, sourceValues: Array\<V>) {
        if (sourceKeys.length != sourceValues.length) {
            throw new core.IllegalStateException("Map 快照键值长度必须相等")
        }
        keys = core.collections.arrayOf\<K>(sourceKeys.length)
        values = core.collections.arrayOf\<V>(sourceValues.length)
        var i: i32 = 0
        while (i < keys.length) {
            keys[i] = core.serialization.deepCopy\<K>((sourceKeys[i] as K))
            values[i] = core.serialization.deepCopy\<V>((sourceValues[i] as V))
            i = (i + 1)
        }
    }

    pub override func iterate(): core.collections.IEnumerator\<Pair\<K, V>> {
        const ks = new core.collections.List\<K>()
        const vs = new core.collections.List\<V>()
        var i: i32 = 0
        while (i < keys.length) {
            ks.add(core.serialization.deepCopy\<K>((keys[i] as K)))
            vs.add(core.serialization.deepCopy\<V>((values[i] as V)))
            i = (i + 1)
        }
        return new core.collections.MapEnumerator\<K, V>(ks, vs)
    }
}

pub shared class AtomicMap\<K with core.serialization.Serializable, V with core.serialization.Serializable> {
    priv const atomic: Atomic\<core.collections.Map\<K, V>>

    priv init(source: core.collections.Map\<K, V>) {
        const copy = new core.collections.Map\<K, V>()
        var i: i64 = (0 as i64)
        while (i < source.count) {
            copy.set(core.serialization.deepCopy\<K>((source.keyAtIndex(i) as K)),
                core.serialization.deepCopy\<V>((source.valueAtIndex(i) as V)))
            i = (i + (1 as i64))
        }
        atomic = unsafe seq { new Atomic\<core.collections.Map\<K, V>>(copy) }
    }

    pub static func fromMap\<A with core.serialization.Serializable, B with core.serialization.Serializable>(source: core.collections.Map\<A, B>): AtomicMap\<A, B> {
        return new AtomicMap\<A, B>(source)
    }

    pub async func set(key: K, value: V) {
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            items.set(core.serialization.deepCopy\<K>(key), core.serialization.deepCopy\<V>(value))
            return@_ items
        }}) }
    }

    pub async func tryGet(key: K): V? {
        var result: V? = null
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            const value = items.tryGet(key)
            if (value != null) { result = core.serialization.deepCopy\<V>((value as V)) }
            return@_ items
        }}) }
        return result
    }

    pub async func containsKey(key: K): bool {
        var result: bool = false
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            result = items.containsKey(key)
            return@_ items
        }}) }
        return result
    }

    pub async func remove(key: K): bool {
        var result: bool = false
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            result = items.remove(key)
            return@_ items
        }}) }
        return result
    }

    pub async func count(): i64 {
        var result: i64 = (0 as i64)
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            result = items.count
            return@_ items
        }}) }
        return result
    }

    pub async func keyAtIndex(index: i64): K? {
        var result: K? = null
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            const value = items.keyAtIndex(index)
            if (value != null) { result = core.serialization.deepCopy\<K>((value as K)) }
            return@_ items
        }}) }
        return result
    }

    pub async func valueAtIndex(index: i64): V? {
        var result: V? = null
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            const value = items.valueAtIndex(index)
            if (value != null) { result = core.serialization.deepCopy\<V>((value as V)) }
            return@_ items
        }}) }
        return result
    }

    pub async func iterate(): AtomicMapSnapshot\<K, V> {
        var result: AtomicMapSnapshot\<K, V>? = null
        unsafe seq { atomic.mutate(func{ (items: core.collections.Map\<K, V>): core.collections.Map\<K, V> -> {
            const ks = core.collections.arrayOf\<K>((items.count as i32))
            const vs = core.collections.arrayOf\<V>((items.count as i32))
            var i: i32 = 0
            while (i < ks.length) {
                ks[i] = (items.keyAtIndex((i as i64)) as K)
                vs[i] = (items.valueAtIndex((i as i64)) as V)
                i = (i + 1)
            }
            result = new AtomicMapSnapshot\<K, V>(ks, vs)
            return@_ items
        }}) }
        return (result as AtomicMapSnapshot\<K, V>)
    }
}


// 快照只保存独立元素；每次枚举再复制，游标与返回对象均不反向修改快照。
pub shared class AtomicSnapshot\<T with core.serialization.Serializable> implements core.collections.IEnumerable\<T> {
    priv const items: Array\<T>

    pub init(source: Array\<T>) {
        items = core.collections.arrayOf\<T>(source.length)
        var i: i32 = 0
        while (i < source.length) {
            items[i] = core.serialization.deepCopy\<T>((source[i] as T))
            i = (i + 1)
        }
    }

    pub override func iterate(): core.collections.IEnumerator\<T> {
        const copy = core.collections.arrayOf\<T>(items.length)
        var i: i32 = 0
        while (i < items.length) {
            copy[i] = core.serialization.deepCopy\<T>((items[i] as T))
            i = (i + 1)
        }
        return new core.collections.ListEnumerator\<T>(copy, copy.length)
    }
}

pub shared class AtomicArray\<T with core.serialization.Serializable> {
    priv const atomic: Atomic\<Array\<T>>

    priv init(source: Array\<T>) {
        const copy = core.collections.arrayOf\<T>(source.length)
        var i: i32 = 0
        while (i < source.length) {
            const element = (source[i] as T)
            const cloned = core.serialization.deepCopy\<T>(element)
            copy[i] = cloned
            i = (i + 1)
        }
        atomic = unsafe seq { new Atomic\<Array\<T>>(copy) }
    }

    pub static func fromArray\<E with core.serialization.Serializable>(source: Array\<E>): AtomicArray\<E> {
        return new AtomicArray\<E>(source)
    }

    pub async func length(): i32 {
        var result: i32 = 0
        unsafe seq { atomic.mutate(func{ (items: Array\<T>): Array\<T> -> {
            result = items.length
            return@_ items
        }}) }
        return result
    }

    pub async func getAtIndex(index: i32): T? {
        var result: T? = null
        unsafe seq { atomic.mutate(func{ (items: Array\<T>): Array\<T> -> {
            const value = items[index]
            if (value != null) { result = core.serialization.deepCopy\<T>((value as T)) }
            return@_ items
        }}) }
        return result
    }

    pub async func setAtIndex(index: i32, value: T) {
        unsafe seq { atomic.mutate(func{ (items: Array\<T>): Array\<T> -> {
            items[index] = core.serialization.deepCopy\<T>(value)
            return@_ items
        }}) }
    }

    pub async func iterate(): AtomicSnapshot\<T> {
        var result: AtomicSnapshot\<T>? = null
        unsafe seq { atomic.mutate(func{ (items: Array\<T>): Array\<T> -> {
            result = new AtomicSnapshot\<T>(items)
            return@_ items
        }}) }
        return (result as AtomicSnapshot\<T>)
    }
}

pub shared class AtomicList\<T with core.serialization.Serializable> {
    priv const atomic: Atomic\<core.collections.List\<T>>

    priv init(source: core.collections.List\<T>) {
        const copy = new core.collections.List\<T>()
        var i: i64 = (0 as i64)
        while (i < source.length) {
            copy.add(core.serialization.deepCopy\<T>((source.getAtIndex(i) as T)))
            i = (i + (1 as i64))
        }
        atomic = unsafe seq { new Atomic\<core.collections.List\<T>>(copy) }
    }

    pub static func fromList\<E with core.serialization.Serializable>(source: core.collections.List\<E>): AtomicList\<E> {
        return new AtomicList\<E>(source)
    }

    pub async func length(): i64 {
        var result: i64 = (0 as i64)
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            result = items.length
            return@_ items
        }}) }
        return result
    }

    pub async func getAtIndex(index: i64): T? {
        var result: T? = null
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            const value = items.getAtIndex(index)
            if (value != null) { result = core.serialization.deepCopy\<T>((value as T)) }
            return@_ items
        }}) }
        return result
    }

    pub async func add(value: T) {
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            items.add(core.serialization.deepCopy\<T>(value))
            return@_ items
        }}) }
    }

    pub async func setAtIndex(index: i64, value: T) {
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            items.setAtIndex(index, core.serialization.deepCopy\<T>(value))
            return@_ items
        }}) }
    }

    pub async func removeAt(index: i64) {
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            items.removeAt(index)
            return@_ items
        }}) }
    }

    // 批量丢弃前缀，仅复制剩余槽。调用方可以按容量比例触发，避免逐项
    // removeAt(0) 的平方移动；替换 backing 同时释放所有已删除的引用。
    pub async func removePrefix(count: i64) {
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            if ((count < (0 as i64)) or (count > items.length)) {
                throw new core.OutOfBoundException(count, items.length)
            }
            const copy = new core.collections.List\<T>()
            var i = count
            while (i < items.length) {
                copy.add((items.getAtIndex(i) as T))
                i = (i + (1 as i64))
            }
            return@_ copy
        }}) }
    }

    pub async func iterate(): AtomicSnapshot\<T> {
        var result: AtomicSnapshot\<T>? = null
        unsafe seq { atomic.mutate(func{ (items: core.collections.List\<T>): core.collections.List\<T> -> {
            const copy = core.collections.arrayOf\<T>((items.length as i32))
            var i: i32 = 0
            while (i < copy.length) {
                copy[i] = (items.getAtIndex((i as i64)) as T)
                i = (i + 1)
            }
            result = new AtomicSnapshot\<T>(copy)
            return@_ items
        }}) }
        return (result as AtomicSnapshot\<T>)
    }
}
