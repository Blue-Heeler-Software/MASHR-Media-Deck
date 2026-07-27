plugins { id("com.android.application") }

android {
    namespace = "com.mediadeck.remote"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.mediadeck.remote"
        minSdk = 26
        targetSdk = 36
        versionCode = 22
        versionName = "1.9.12"
    }

    buildFeatures { buildConfig = true }
}
