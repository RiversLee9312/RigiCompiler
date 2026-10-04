using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // Fixtures.FileSystem 职责；与主文件共享同一类型、字段及生命周期。

        // Linux 原生 fs_rename 机制探针：直接调用最终链接进 native 产物的
        // rigi_rt 符号，在套件自建唯一目录内断言目录源 Replace 系统保证。
        // 不注入生产竞态钩子；Barrier 双线程只是制造真实内核争用，
        // 已有目标空目录是无需碰调度运气的确定性判别例。
        private const string FsReplaceNativeProbe = """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <pthread.h>
            #include <stdint.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <sys/stat.h>
            #include <time.h>
            #include <unistd.h>

            typedef struct { const char *data; int64_t len; } rigi_string;
            extern int32_t rigi_fs_rename(const rigi_string *, const rigi_string *, int32_t);
            static int move_path(const char *src, const char *dst, int replace) {
                rigi_string a = {src, (int64_t)strlen(src)};
                rigi_string b = {dst, (int64_t)strlen(dst)};
                return rigi_fs_rename(&a, &b, replace);
            }
            static int make_path(char *out, const char *root, const char *name) {
                int n = snprintf(out, 1024, "%s/%s", root, name);
                return n > 0 && n < 1024;
            }
            static int put_file(const char *path, const char *text) {
                FILE *f = fopen(path, "w");
                if (!f) return 0;
                int ok = fputs(text, f) >= 0;
                return fclose(f) == 0 && ok;
            }
            static int file_is(const char *path, const char *text) {
                char buf[64] = {0};
                FILE *f = fopen(path, "r");
                if (!f) return 0;
                int ok = fgets(buf, sizeof buf, f) && strcmp(buf, text) == 0;
                fclose(f);
                return ok;
            }
            static int is_dir(const char *path) {
                struct stat st;
                return lstat(path, &st) == 0 && S_ISDIR(st.st_mode);
            }
            static int fail(const char *what, int rc) {
                fprintf(stderr, "fs_rename %s: rc=%d errno=%d\n", what, rc, errno);
                return 1;
            }
            typedef struct {
                pthread_barrier_t *barrier;
                const char *src;
                const char *dst;
                int rc;
            } mover_arg;
            static void *move_thread(void *raw) {
                mover_arg *arg = (mover_arg *)raw;
                int wait = pthread_barrier_wait(arg->barrier);
                arg->rc = wait == 0 || wait == PTHREAD_BARRIER_SERIAL_THREAD
                    ? move_path(arg->src, arg->dst, 1) : -999;
                return NULL;
            }
            static int join_before(pthread_t thread) {
                struct timespec limit;
                if (clock_gettime(CLOCK_REALTIME, &limit)) return 0;
                limit.tv_sec += 30;
                return pthread_timedjoin_np(thread, NULL, &limit) == 0;
            }
            int rigi_fs_replace_native_probe(void) {
                const char *root = getenv("RIGI_FS_PROBE_ROOT");
                if (!root) return fail("fixture root", -1);
                char src[1024], dst[1024], child[1024], source2[1024];
                if (!make_path(src, root, "src_dir") || !make_path(dst, root, "dst_dir")
                    || !make_path(child, root, "src_dir/marker")) return fail("path", -1);
                if (mkdir(src, 0700) || mkdir(dst, 0700) || !put_file(child, "source"))
                    return fail("setup directory", -1);
                int rc = move_path(src, dst, 1);
                if (rc != -21 || !is_dir(dst) || !file_is(child, "source"))
                    return fail("existing empty directory", rc);
                if (!make_path(dst, root, "dst_file") || !put_file(dst, "old"))
                    return fail("setup file", -1);
                rc = move_path(src, dst, 1);
                if (rc != -17 || !file_is(dst, "old") || !file_is(child, "source"))
                    return fail("directory over file", rc);
                if (!make_path(dst, root, "dst_link") || symlink("missing", dst))
                    return fail("setup broken link", -1);
                rc = move_path(src, dst, 1);
                struct stat st;
                if (rc != -17 || lstat(dst, &st) || !S_ISLNK(st.st_mode)
                    || !file_is(child, "source")) return fail("directory over link", rc);
                if (!make_path(src, root, "file_src") || !make_path(dst, root, "file_dst")
                    || !put_file(src, "new") || !put_file(dst, "old"))
                    return fail("setup file overwrite", -1);
                rc = move_path(src, dst, 1);
                if (rc || access(src, F_OK) == 0 || !file_is(dst, "new"))
                    return fail("file overwrite", rc);
                if (!make_path(src, root, "link_src") || !make_path(dst, root, "link_dst")
                    || symlink("new_missing", src) || symlink("old_missing", dst))
                    return fail("setup links", -1);
                rc = move_path(src, dst, 1);
                char link_target[64] = {0};
                ssize_t len = readlink(dst, link_target, sizeof link_target - 1);
                if (rc || len != 11 || strcmp(link_target, "new_missing") != 0)
                    return fail("link entry overwrite", rc);

                // 两个稳定目录同时争用缺失目标：内核只能接受一个移动。
                if (!make_path(src, root, "race_a") || !make_path(source2, root, "race_b")
                    || !make_path(dst, root, "race_dst") || mkdir(src, 0700)
                    || mkdir(source2, 0700)) return fail("setup race", -1);
                pthread_barrier_t barrier;
                pthread_t a, b;
                if (pthread_barrier_init(&barrier, NULL, 2)) return fail("barrier", -1);
                mover_arg one = {&barrier, src, dst, -999};
                mover_arg two = {&barrier, source2, dst, -999};
                if (pthread_create(&a, NULL, move_thread, &one)
                    || pthread_create(&b, NULL, move_thread, &two))
                    return fail("thread start", -1);
                if (!join_before(a) || !join_before(b)) return fail("thread timeout", -1);
                pthread_barrier_destroy(&barrier);
                if (!((one.rc == 0 && two.rc == -21 && is_dir(source2))
                    || (two.rc == 0 && one.rc == -21 && is_dir(src)))
                    || !is_dir(dst)) return fail("directory race", one.rc);
                puts("fs-replace-native-ok");
                return 0;
            }
            """;

        // native-only：独立 statx 真值与 rigi_rt 的 48B 结构逐字段对照。
        // 受控 mtime 明显早于 birth；procfs 无 STATX_BTIME 时必须为哨兵。
        private const string FsBirthNativeProbe = """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <inttypes.h>
            #include <linux/stat.h>
            #include <stdint.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <sys/stat.h>
            #include <sys/sysmacros.h>
            #include <sys/types.h>
            #include <time.h>
            #include <unistd.h>

            typedef struct { const char *data; int64_t len; } rigi_string;
            typedef struct { uint64_t type_id, payload; } RigiFatRef;
            extern int32_t rigi_fs_stat(const rigi_string *, const RigiFatRef *);
            extern int32_t rigi_fs_lstat(const rigi_string *, const RigiFatRef *);
            static int read_native(const char *path, int follow, uint8_t out[80]) {
                memset(out, 0, 80);
                int32_t length = 48;
                memcpy(out + 24, &length, 4);
                rigi_string p = {path, (int64_t)strlen(path)};
                RigiFatRef buf = {0, (uintptr_t)out};
                int rc = follow ? rigi_fs_stat(&p, &buf) : rigi_fs_lstat(&p, &buf);
                if (!rc) memmove(out, out + 32, 48);
                return rc;
            }
            static int64_t i64_at(const uint8_t *p, int offset) {
                uint64_t v = 0;
                for (int i = 0; i < 8; i++) v |= (uint64_t)p[offset + i] << (8 * i);
                return (int64_t)v;
            }
            static int32_t i32_at(const uint8_t *p, int offset) {
                uint32_t v = 0;
                for (int i = 0; i < 4; i++) v |= (uint32_t)p[offset + i] << (8 * i);
                return (int32_t)v;
            }
            static int fail(const char *name) {
                fprintf(stderr, "fs-birth native mismatch: %s\n", name);
                return 1;
            }
            int rigi_fs_birth_native_probe(void) {
                const char *root = getenv("RIGI_FS_PROBE_ROOT");
                if (!root) return fail("fixture root");
                char file[1024], link[1024];
                if (snprintf(file, sizeof file, "%s/fixture", root) >= sizeof file
                    || snprintf(link, sizeof link, "%s/link", root) >= sizeof link)
                    return fail("path");
                FILE *f = fopen(file, "wb");
                if (!f || fputc('x', f) == EOF || fclose(f)) return fail("create");
                struct timespec times[2] = {{1700000000, 123456700},
                    {1700000000, 123456700}};
                if (utimensat(AT_FDCWD, file, times, 0)) return fail("utimensat");
                struct stat st;
                struct statx sx;
                if (stat(file, &st) || st.st_mtim.tv_sec != 1700000000
                    || st.st_mtim.tv_nsec != 123456700) return fail("mtime fixture");
                // 测试目录不保证位于 ext4：无 birth 的卷只钉哨兵，
                // 有 STATX_BTIME 才进入真实创建时间逐值断言。
                if (statx(AT_FDCWD, file, 0, STATX_BTIME, &sx)
                    || !(sx.stx_mask & STATX_BTIME)) {
                    uint8_t missing[80];
                    if (read_native(file, 1, missing)
                        || i64_at(missing, 36) != INT64_MIN
                        || i32_at(missing, 44) != 0)
                        return fail("unsupported birth not null");
                    puts("fs-birth-native-ok");
                    return 0;
                }
                if (sx.stx_btime.tv_sec == st.st_mtim.tv_sec)
                    return fail("birth not independent from mtime");
                uint8_t out[80];
                if (read_native(file, 1, out)) return fail("native stat");
                int64_t birth_ms = sx.stx_btime.tv_sec * 1000
                    + sx.stx_btime.tv_nsec / 1000000;
                int32_t birth_ns = sx.stx_btime.tv_nsec % 1000000;
                if (i64_at(out, 36) != birth_ms || i32_at(out, 44) != birth_ns) {
                    fprintf(stderr, "native birth=%" PRId64 "/%" PRId32
                        " statx=%" PRId64 "/%" PRId32 " mask=0x%x"
                        " st dev=%u:%u ino=%" PRIu64
                        " sx dev=%u:%u ino=%" PRIu64 "\n",
                        i64_at(out, 36), i32_at(out, 44), birth_ms, birth_ns,
                        sx.stx_mask, major(st.st_dev), minor(st.st_dev),
                        (uint64_t)st.st_ino, sx.stx_dev_major, sx.stx_dev_minor,
                        sx.stx_ino);
                    return fail("native birth != statx birth");
                }
                if (i64_at(out, 12) != 1700000000123LL
                    || i32_at(out, 20) != 456700)
                    return fail("mtime precision");
                if (symlink("fixture", link)) return fail("symlink");
                if (statx(AT_FDCWD, link, AT_SYMLINK_NOFOLLOW, STATX_BTIME, &sx)
                    || !(sx.stx_mask & STATX_BTIME)) return fail("link birth mask");
                if (read_native(link, 0, out) || i32_at(out, 0) != 2
                    || i64_at(out, 36) != sx.stx_btime.tv_sec * 1000
                        + sx.stx_btime.tv_nsec / 1000000
                    || i32_at(out, 44) != sx.stx_btime.tv_nsec % 1000000)
                    return fail("native lstat link birth");
                if (read_native(link, 1, out) || i32_at(out, 0) != 0
                    || i64_at(out, 36) != birth_ms || i32_at(out, 44) != birth_ns)
                    return fail("native stat follows link");
                // procfs 若明确缺 birth，核真实不可得哨兵；平台若支持
                // procfs birth 则不据平台名猜测，不断言不可得。
                if (!statx(AT_FDCWD, "/proc/self/stat", 0, STATX_BTIME, &sx)
                    && !(sx.stx_mask & STATX_BTIME)) {
                    if (read_native("/proc/self/stat", 1, out)
                        || i64_at(out, 36) != INT64_MIN || i32_at(out, 44) != 0)
                        return fail("proc unsupported birth not null");
                }
                puts("fs-birth-native-ok");
                return 0;
            }
            """;

    }
}
