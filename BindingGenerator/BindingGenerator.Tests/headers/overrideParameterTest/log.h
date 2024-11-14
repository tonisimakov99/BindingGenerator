typedef enum android_LogPriority {
	/** For internal use only.  */
	ANDROID_LOG_UNKNOWN = 0,
	/** The default priority, for internal use only.  */
	ANDROID_LOG_DEFAULT, /* only for SetMinPriority() */
	/** Verbose logging. Should typically be disabled for a release apk. */
	ANDROID_LOG_VERBOSE,
	/** Debug logging. Should typically be disabled for a release apk. */
	ANDROID_LOG_DEBUG,
	/** Informational logging. Should typically be disabled for a release apk. */
	ANDROID_LOG_INFO,
	/** Warning logging. For use with recoverable failures. */
	ANDROID_LOG_WARN,
	/** Error logging. For use with unrecoverable failures. */
	ANDROID_LOG_ERROR,
	/** Fatal logging. For use when aborting. */
	ANDROID_LOG_FATAL,
	/** For internal use only.  */
	ANDROID_LOG_SILENT, /* only for SetMinPriority(); must be last */
} android_LogPriority;

int __android_log_print(int prio, const char* tag, const char* fmt, ...)
__attribute__((__format__(printf, 3, 4)));