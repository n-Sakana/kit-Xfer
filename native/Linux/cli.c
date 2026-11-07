// Invoke the HTTP/help entry in the same Native AOT library, without a .NET host.
#include <dlfcn.h>
#include <stdio.h>
int main(int argc, char **argv) {
    if (argc < 3) return 1;
    void *app = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!app) { fprintf(stderr, "Xfer: %s\n", dlerror()); return 1; }
    int (*run)(int, char **, const char *) = dlsym(app, "xfer_run_cli");
    if (!run) { fprintf(stderr, "Xfer: %s\n", dlerror()); return 1; }
    return run(argc - 3, argv + 3, argv[2]);
}
