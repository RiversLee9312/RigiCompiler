namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        private const string NativeRcLifecycleSource = """
            import core.io.Console
            @NativeLibrary("nrh_test")
            @NativeSymbol("nrh_selftest")
            priv native func selftest(): i32
            pub func main(): i32 {
                const result = selftest()
                if (result != 0) { return result }
                Console.println("native-rc-ok")
                return 0
            }
            """;

        // C 只测试注册表内部计数；语言使用者仍通过 IDisposable 管理 Handle。
        // 每轮真实线程与最终 release 竞争，保有引用时才允许读 payload。
        private const string NativeRcLifecycleC = """
            #include <stdint.h>
            #include <stdlib.h>
            #include <stdatomic.h>
            #ifdef _WIN32
            #include <windows.h>
            #else
            #include <pthread.h>
            #endif
            extern int64_t rigi_native_rc_create(void *, void (*)(void *));
            extern int32_t rigi_native_rc_retain(int64_t);
            extern void rigi_native_rc_release(int64_t);
            extern void *rigi_native_rc_payload_of(int64_t, const char *);
            typedef struct { int marker; int64_t child; } Payload;
            static _Atomic int destroyed;
            static _Atomic int ready;
            static _Atomic int go;
            static int64_t current;
            static void require(int ok) { if (!ok) abort(); }
            static void destroy(void *p) {
                Payload *v = (Payload *)p;
                require(v->marker == 42);
                if (v->child) rigi_native_rc_release(v->child);
                atomic_fetch_add(&destroyed, 1);
                free(v);
            }
            static int64_t create(int64_t child) {
                Payload *p = (Payload *)malloc(sizeof(Payload));
                require(p != NULL);
                p->marker = 42;
                p->child = child;
                return rigi_native_rc_create(p, destroy);
            }
            #ifdef _WIN32
            static DWORD WINAPI worker(void *unused)
            #else
            static void *worker(void *unused)
            #endif
            {
                (void)unused;
                atomic_fetch_add(&ready, 1);
                while (!atomic_load(&go)) { }
                for (int i = 0; i < 512; ++i) {
                    if (!rigi_native_rc_retain(current)) continue;
                    Payload *p = (Payload *)rigi_native_rc_payload_of(current, "nrh_test");
                    require(p->marker == 42);
                    rigi_native_rc_release(current);
                }
                return 0;
            }
            int32_t nrh_selftest(void) {
                require(!rigi_native_rc_retain(0));
                require(!rigi_native_rc_retain(-1));
                require(!rigi_native_rc_retain(INT64_MAX));
                int64_t previous = 0;
                for (int round = 0; round < 64; ++round) {
                    current = create(0);
                    require(current > previous);
                    require(!rigi_native_rc_retain(previous));
                    require(rigi_native_rc_retain(current));
                    rigi_native_rc_release(current);
                    require(atomic_load(&destroyed) == round);
                    atomic_store(&ready, 0);
                    atomic_store(&go, 0);
                    #ifdef _WIN32
                    HANDLE threads[4];
                    for (int i = 0; i < 4; ++i) {
                        threads[i] = CreateThread(NULL, 0, worker, NULL, 0, NULL);
                        require(threads[i] != NULL);
                    }
                    #else
                    pthread_t threads[4];
                    for (int i = 0; i < 4; ++i)
                        require(pthread_create(&threads[i], NULL, worker, NULL) == 0);
                    #endif
                    while (atomic_load(&ready) != 4) { }
                    atomic_store(&go, 1);
                    rigi_native_rc_release(current);
                    #ifdef _WIN32
                    require(WaitForMultipleObjects(4, threads, TRUE, INFINITE) == WAIT_OBJECT_0);
                    for (int i = 0; i < 4; ++i) CloseHandle(threads[i]);
                    #else
                    for (int i = 0; i < 4; ++i) require(pthread_join(threads[i], NULL) == 0);
                    #endif
                    require(!rigi_native_rc_retain(current));
                    require(atomic_load(&destroyed) == round + 1);
                    previous = current;
                }
                /* 父析构内释放子资源：若析构仍持注册表锁，此处会死锁。 */
                int64_t child = create(0);
                int64_t parent = create(child);
                rigi_native_rc_release(parent);
                require(!rigi_native_rc_retain(parent));
                require(!rigi_native_rc_retain(child));
                require(atomic_load(&destroyed) == 66);
                return 0;
            }
            """;
    }
}
