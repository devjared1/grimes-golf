# Putting Grimes Golf on TestFlight

How to go from the test build in [ipad-testing.md](ipad-testing.md) to a signed build that installs on an iPad through
Apple's TestFlight app: no cable, no Sideloadly, no 7-day expiry, and testers of your choosing. All of it is done from
a Windows PC (or an iPad's browser) and GitHub Actions; there's no Mac in any step.

**This has not been tried.** It was written before the Apple Developer account existed, from Apple's documentation and
from how the tools work, so there was nothing to run the signing job against. Apple's websites change their wording
and menus from time to time: where a name here doesn't match the screen, look for the nearest one. The first real run
will find the mistakes, and when it does, fix this page too. [What's untested](#whats-untested) lists the weakest
parts.

## Contents

- [What you need](#what-you-need)
- [1. Join the Apple Developer Program](#1-join-the-apple-developer-program)
- [2. Register the app](#2-register-the-app)
- [3. Make the signing certificate](#3-make-the-signing-certificate)
- [4. Make the provisioning profile](#4-make-the-provisioning-profile)
- [5. Make an App Store Connect API key](#5-make-an-app-store-connect-api-key)
- [6. Give GitHub the secrets](#6-give-github-the-secrets)
- [7. Run the release build](#7-run-the-release-build)
- [8. Testers](#8-testers)
- [Keeping it going](#keeping-it-going)
- [What's untested](#whats-untested)
- [Beyond TestFlight](#beyond-testflight)

## What you need

- The Apple Developer Program: US$99 a year. It's a separate sign-up from a free Apple ID, and Apple can take a day or
  two to approve it.
- An Apple ID with two-factor authentication on.
- A Windows PC with **Git for Windows** (its `openssl.exe` is used below, in a Git Bash window) and PowerShell.
- Admin rights on the GitHub repository, to add secrets.

## 1. Join the Apple Developer Program

1. Go to [developer.apple.com/programs/enroll](https://developer.apple.com/programs/enroll/) and follow it with your
   Apple ID. As an individual, the account is in your name; as an organisation it needs a D-U-N-S number and takes
   longer.
2. When it's approved, open [developer.apple.com/account](https://developer.apple.com/account) and note the **Team ID**
   under **Membership details**: ten letters and digits. That's the `IOS_TEAM_ID` secret.

## 2. Register the app

**The bundle identifier** is the app's permanent name on Apple's side, like `com.yourname.grimesgolf`. It can't be
changed after the app is made, and the placeholder in the project, `com.grimesgolf.app`, is only a placeholder.

1. [developer.apple.com/account](https://developer.apple.com/account) > **Certificates, Identifiers & Profiles** >
   **Identifiers** > **+** > **App IDs** > **App**.
2. Give it a description ("Grimes Golf"), choose **Explicit** and type the bundle identifier. Leave the capabilities
   alone and register it.
3. In GitHub, repository **Settings > Secrets and variables > Actions > Variables** tab > **New repository variable**:
   `IOS_BUNDLE_ID` = the bundle identifier. (It's a variable, not a secret: it's printed inside every build anyway.
   The unsigned build uses it too once it's set.)

**The app's record** in App Store Connect is what TestFlight builds belong to.

1. [appstoreconnect.apple.com](https://appstoreconnect.apple.com) > **Apps** > **+** > **New App**.
2. Platform **iOS**, a name (it must be unique across the App Store, so "Grimes Golf" may be taken; it can be changed
   later), a language, the bundle identifier from above, and any SKU (it's only for your own use: `grimesgolf`).
3. The app is iPad-only. TestFlight takes that from the build itself.

## 3. Make the signing certificate

An **Apple Distribution** certificate proves a build came from you. Making one needs a *certificate signing request*,
which Apple's instructions make on a Mac; OpenSSL makes the same thing on Windows. The private key made here is what
signs your apps: keep it in a safe place, never in the repository, and don't send it to anyone.

In **Git Bash** (Start > Git Bash), in a new empty folder outside the repository:

```bash
"/c/Program Files/Git/usr/bin/openssl.exe" genrsa -out grimesgolf.key 2048
"/c/Program Files/Git/usr/bin/openssl.exe" req -new -key grimesgolf.key -out grimesgolf.csr \
    -subj "/emailAddress=you@example.com/CN=Your Name/C=AU"
```

(Your own email address, name and two-letter country code.) Then:

1. **Certificates, Identifiers & Profiles > Certificates > +** > **Apple Distribution** > Continue.
2. Upload `grimesgolf.csr`, continue, and **Download** the certificate. It's `distribution.cer`; put it in the same
   folder.
3. Back in Git Bash, make the `.p12` file that holds the certificate and its key together, and choose a password for
   it when asked:

```bash
"/c/Program Files/Git/usr/bin/openssl.exe" x509 -inform DER -in distribution.cer -out distribution.pem
"/c/Program Files/Git/usr/bin/openssl.exe" pkcs12 -export -inkey grimesgolf.key -in distribution.pem \
    -out distribution.p12 -name "Grimes Golf distribution" \
    -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1
```

(The three extra options make an older style of file that macOS's own tools read reliably; newer OpenSSL's default
style has been a problem for them.) The password is the `IOS_CERTIFICATE_PASSWORD` secret.

To put the file into a secret, PowerShell turns it into text and puts it on the clipboard:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("distribution.p12")) | Set-Clipboard
```

That's the `IOS_CERTIFICATE_P12` secret: paste it when [step 6](#6-give-github-the-secrets) asks.

## 4. Make the provisioning profile

1. **Certificates, Identifiers & Profiles > Profiles > +**.
2. Under **Distribution**, choose **App Store Connect** (older name: App Store) > Continue.
3. Pick the app's identifier from step 2, then the distribution certificate from step 3, name the profile ("Grimes
   Golf App Store") and generate it.
4. **Download** it (`Grimes_Golf_App_Store.mobileprovision`) and turn it into text the same way:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("Grimes_Golf_App_Store.mobileprovision")) | Set-Clipboard
```

That's the `IOS_PROVISIONING_PROFILE` secret.

## 5. Make an App Store Connect API key

This lets GitHub upload builds without your Apple ID or password.

1. [appstoreconnect.apple.com](https://appstoreconnect.apple.com) > **Users and Access** > **Integrations** >
   **App Store Connect API** > **Team Keys** > **Generate API Key** (or **+**).
2. Name it ("GitHub builds"), give it the **App Manager** role, and generate it.
3. **Download API Key** now: Apple lets you download the `.p8` file **once only**. Note the **Key ID** (in its row)
   and the **Issuer ID** (at the top of the page, a long hyphenated code).
4. Turn the file into text:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("AuthKey_XXXXXXXXXX.p8")) | Set-Clipboard
```

## 6. Give GitHub the secrets

Repository **Settings > Secrets and variables > Actions > Secrets** tab > **New repository secret**, for each:

| Name | What it is | Where it came from |
|---|---|---|
| `IOS_TEAM_ID` | The ten-character team ID | Step 1 |
| `IOS_CERTIFICATE_P12` | The `.p12` file as text | Step 3 |
| `IOS_CERTIFICATE_PASSWORD` | The password chosen for the `.p12` | Step 3 |
| `IOS_PROVISIONING_PROFILE` | The `.mobileprovision` file as text | Step 4 |
| `ASC_KEY_ID` | The API key's Key ID | Step 5 |
| `ASC_ISSUER_ID` | The Issuer ID | Step 5 |
| `ASC_KEY_P8` | The `.p8` file as text | Step 5 |

and the variable `IOS_BUNDLE_ID` from step 2 (Variables tab, not Secrets).

Secrets can't be read back once saved, only replaced. Then delete the clipboard contents and, once you're sure of them,
keep the `.key`, `.p12`, `.p8` and the password in a password manager or a backup, and not in a synced or shared
folder. The workflow never prints them, and hides the certificate's name (which has yours in it) in its log.

## 7. Run the release build

1. Merge the iPad branch first: GitHub only shows **Run workflow** for workflows on the default branch.
2. **Actions** > **iPad release (TestFlight)** > **Run workflow**. Leave **Send the build to TestFlight** on. (Turn it
   off for a first try that only checks the signing works, and nothing is uploaded.)
3. It takes about 10 minutes. If a secret is missing the first step says which.
4. Apple then processes the build, usually in 5 to 30 minutes, and emails you if it finds a problem with it. It then
   appears in App Store Connect > your app > **TestFlight**.

The build number is the run's number (`github.run_number`), which only goes up, as Apple requires for every upload of a
version. The version is the game's, from `project.godot` (`1.0.0-beta` goes in as `1.0.0`).

The unsigned **iPad build** workflow, used for pull requests and for Sideloadly, is separate and keeps working without
any of this.

## 8. Testers

In App Store Connect > your app > **TestFlight**:

- **Internal testing** (up to 100 people): people you've added in **Users and Access**, with an Apple ID. Builds
  appear for them as soon as they've processed, with no review. Make a group, add the build and the people.
  Start here: it's you, and anyone in your household you add.
- **External testing** (up to 10,000): anyone, by email or a public link. The first build of each version goes
  through Apple's beta review (usually a day or less), and Apple asks for some test information first: what to test,
  a contact, and possibly a privacy policy address.

Testers install Apple's free **TestFlight** app on the iPad, accept the invitation (or open the link) and tap
**Install**. TestFlight builds stop working 90 days after they're uploaded, so a new one is needed by then. Testers
are told when one's available.

Logs come back the same way as for the Sideloadly build: through the Files app
([ipad-testing.md](ipad-testing.md#sending-back-the-logs)).

## Keeping it going

- **The certificate and profile last a year.** Before then, redo steps 3 and 4 (a new certificate can use the same
  `.key`, and a new `.csr` isn't needed) and replace the three secrets. A build signed with an expired one fails to
  sign, and ones already installed from TestFlight keep working until their 90 days.
- **The $99 renews yearly.** If it lapses, builds in TestFlight stop being available and uploads are refused.
- **A leaked secret:** revoke the certificate (Certificates page) or the API key (Integrations page) at Apple, make a
  new one, and replace the secret. Neither can do anything without the other secrets, but don't rely on that.

## What's untested

Everything on this page was written without an account to run it against.

- **Never run:** the `iPad release (TestFlight)` workflow, and the signed path of `tools/ios-export.sh`. The unsigned
  path, which they share their setup with, is tested, on every pull request.
- **The weakest guesses:** the OpenSSL `.p12` options (macOS can reject a `.p12` that it dislikes: the failure would be
  at the "Install the certificate" step); that `xcodebuild -exportArchive` accepts the API key to upload directly, with
  the manual signing set in the export options; and that Godot's export, which here is told the real team ID, still
  makes an Xcode project that signs with the named profile. If the upload step is the one that fails, the fallback is
  to export the `.ipa` and upload it with `xcrun altool --upload-package` and the same key. Apple's own page for that
  tool marks the older `--upload-app` as deprecated, and Xcode 26 has had reported upload bugs.
- **Apple's screens and wording** may differ from the names here.

## Beyond TestFlight

Not built or covered here, but found on the way:

- An App Store release is a separate step with its own requirements: screenshots at iPad sizes, an age rating,
  a privacy policy and the privacy questionnaire (the app keeps its data on the device and talks to other players
  only in online play), and Apple's review.
- Review has to be able to try the app, so Bluetooth launch monitor support (untested with real monitors) and the keyboard-only
  features would count against it; so would the placeholder `com.grimesgolf.app` bundle identifier if it's still there.
- `NSLocalNetworkUsageDescription` and `ITSAppUsesNonExemptEncryption` are already set in the export preset, which
  saves two common rejections and the export-compliance question on every upload.
