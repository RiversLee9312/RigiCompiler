namespace core

// unsafe 共享边界只保存 capability；普通 T 不进入共享对象字段闭包。
pub unsafe shared class Atomic\<T> {
    priv var current: Handle\<T>
    priv const mutex: core.coroutine.Mutex = new core.coroutine.Mutex()

    pub unsafe init(value: T) {
        current = seq using(const place = placeOf value) {
            return@_ place.expose()
        }
    }

    pub unsafe func load(): T {
        const lock = await mutex.acquire()
        try { return current.load() }
        finally(e) { mutex.release(lock) }
    }

    pub unsafe func mutate(body: Func\<T, T>) {
        const lock = await mutex.acquire()
        try {
            const old = current.load()
            const next = body(old)
            // 成功返回后才替换 capability；对象可替换，异常保留旧值。
            seq using(const place = placeOf next) {
                current = place.expose()
            }
        } finally(e) { mutex.release(lock) }
    }
}

// 安全值类型门面不向调用者暴露 capability（Atomic/Handle/Cell 均不可见）。
pub shared class AtomicStruct\<T extends ValueType> {
    priv const atomic: Atomic\<T>

    pub init(value: T) {
        atomic = unsafe seq { new Atomic\<T>(value) }
    }

    pub func load(): T {
        unsafe seq { return atomic.load() }
    }

    pub func store(value: T) {
        unsafe seq { atomic.mutate(func{ (old: T): T -> value }) }
    }

    // 一步式安全 RMW：语义完全复用私有 Atomic.mutate——Mutex 持锁回调，
    // 回调成功返回后才替换 Handle，回调抛错保留旧值。回调是普通 Func，
    // 运行在当前协程，内部可 await/yield 挂起，锁仍保持。
    // 安全写法替代易丢更新的 store(load() + 1) 形态。
    pub func mutate(body: Func\<T, T>) {
        unsafe seq { atomic.mutate(body) }
    }
}
