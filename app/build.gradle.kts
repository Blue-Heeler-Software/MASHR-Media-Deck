plugins { id("com.android.application") }

android {
    namespace = "com.mediadeck.remote"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.mediadeck.remote"
        minSdk = 26
        targetSdk = 36
        versionCode = 13
        versionName = "1.4.5"
    }

    buildFeatures { buildConfig = true }
}
