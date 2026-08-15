plugins { id("com.android.application") }

val releaseKeystoreFile = providers.environmentVariable("MASHR_KEYSTORE_FILE").orNull
val releaseKeystorePassword = providers.environmentVariable("MASHR_KEYSTORE_PASSWORD").orNull
val releaseKeyAlias = providers.environmentVariable("MASHR_KEY_ALIAS").orNull
val releaseKeyPassword = providers.environmentVariable("MASHR_KEY_PASSWORD").orNull
val releaseSigningReady = listOf(releaseKeystoreFile, releaseKeystorePassword, releaseKeyAlias, releaseKeyPassword).all { !it.isNullOrBlank() }

android {
    namespace = "com.mediadeck.remote"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.mediadeck.remote"
        minSdk = 26
        targetSdk = 36
        versionCode = 26
        versionName = "1.9.16"
    }

    buildFeatures { buildConfig = true }

    signingConfigs {
        if (releaseSigningReady) {
            create("release") {
                storeFile = file(releaseKeystoreFile!!)
                storePassword = releaseKeystorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        getByName("release") {
            isDebuggable = false
            isMinifyEnabled = false
            signingConfigs.findByName("release")?.let { signingConfig = it }
        }
    }
}

gradle.taskGraph.whenReady {
    val packagesRelease = allTasks.any { it.name == "assembleRelease" || it.name == "bundleRelease" || it.name == "packageRelease" }
    if (packagesRelease && !releaseSigningReady) {
        throw GradleException("Release packaging requires MASHR_KEYSTORE_FILE, MASHR_KEYSTORE_PASSWORD, MASHR_KEY_ALIAS, and MASHR_KEY_PASSWORD.")
    }
}
