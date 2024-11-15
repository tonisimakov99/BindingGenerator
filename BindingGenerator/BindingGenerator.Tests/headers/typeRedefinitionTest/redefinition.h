#if defined(__LP64__)
typedef long __intptr_t;
typedef unsigned long __uintptr_t;
#else
typedef int __intptr_t;
typedef unsigned int __uintptr_t;
#endif