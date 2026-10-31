/*
 * The only native piece of Renaissance.Clap.
 *
 * CLAP requires the plugin binary to export a data symbol named "clap_entry" whose layout is
 * clap_plugin_entry_t. NativeAOT can export functions (UnmanagedCallersOnly) but not data, so this
 * tiny C object is compiled and linked into the NativeAOT DLL. It owns the struct and forwards the
 * three function pointers to the managed exports in Exports.cs.
 *
 * Keep the version in sync with ClapConstants.Version* (CLAP 1.2.10).
 */

/* No CRT headers on purpose: cl.exe can then compile this without a vcvars environment. */
typedef struct clap_version {
   unsigned int major;
   unsigned int minor;
   unsigned int revision;
} clap_version_t;

typedef struct clap_plugin_entry {
   clap_version_t clap_version;
   unsigned char (*init)(const char *plugin_path);
   void (*deinit)(void);
   const void *(*get_factory)(const char *factory_id);
} clap_plugin_entry_t;

/* Provided by the managed assembly (Exports.cs). cdecl is the default on x64. */
extern unsigned char renaissance_clap_init(const char *plugin_path);
extern void renaissance_clap_deinit(void);
extern const void *renaissance_clap_get_factory(const char *factory_id);

__declspec(dllexport) const clap_plugin_entry_t clap_entry = {
   { 1, 2, 10 },
   renaissance_clap_init,
   renaissance_clap_deinit,
   renaissance_clap_get_factory
};
