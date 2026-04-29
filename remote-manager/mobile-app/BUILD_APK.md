# How to Build the Android `.apk`

This guide produces an `.apk` file you can install directly on any Android phone — **no Play Store needed**.

---

## How It Works

We use **Expo EAS Build** — Expo's cloud build service. Their servers compile the APK for you.
- ✅ Free tier available
- ✅ No Android Studio required on your machine
- ✅ No Java / JDK needed
- ✅ Works from Windows

---

## Step 1 — Create a Free Expo Account (one-time)

Go to [https://expo.dev/signup](https://expo.dev/signup) and create a free account.
Remember your username/email — you'll need it below.

---

## Step 2 — Install EAS CLI

Open PowerShell and run:

```powershell
npm install -g eas-cli
```

---

## Step 3 — Login to Expo

```powershell
eas login
```

Enter your Expo account email and password when prompted.

---

## Step 4 — Install Dependencies

```powershell
cd C:\Users\sajid\Desktop\Freelacing\actools\remote-manager\mobile-app
npm install
```

---

## Step 5 — Configure EAS Project (one-time)

```powershell
eas build:configure
```

When asked **"Which platforms would you like to configure?"** → select `Android`.
This updates your `app.json` with your Expo project ID.

---

## Step 6 — Build the APK

```powershell
eas build --platform android --profile preview
```

- This uploads your code to Expo's servers and builds the APK in the cloud
- **Takes 5–15 minutes** (you can close the terminal — it builds in the background)
- You'll get a **download link** in the terminal when done

You can also check build status at: [https://expo.dev/accounts/YOUR_USERNAME/projects/ac-remote-manager/builds](https://expo.dev)

---

## Step 7 — Download & Install the APK

1. Download the `.apk` file from the link in the terminal (or from expo.dev dashboard)
2. Send it to the phone (WhatsApp, email, USB, Google Drive — anything works)
3. On the Android phone:
   - Go to **Settings → Security → Install unknown apps** → Allow for your browser/files app
   - Open the `.apk` → Install
4. Open **AC Remote Manager** app

---

## Step 8 — First Launch on Phone

On first launch, the app will show a **Settings screen** asking for the server URL:

```
http://192.168.1.100:4000
```

Enter the **LAN IP address of the boss PC** (the machine running the server).
Tap **Save & Connect** — the app will auto-authenticate and show all gaming PCs.

> To find the boss PC's LAN IP: open PowerShell on the boss PC → `ipconfig`
> Look for `IPv4 Address` under the WiFi adapter.

The IP is saved permanently — you only enter it once.

---

## Changing the Server IP Later

Tap the **⚙** (gear) icon in the top-right corner of the app at any time to update the server IP.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| "Cannot reach server" | Make sure boss PC server is running; check the IP is correct |
| "Bypass auth not enabled" | Add `BYPASS_AUTH=true` to `remote-manager/server/.env` |
| APK won't install | Enable "Install from unknown sources" in Android settings |
| EAS build fails | Run `eas whoami` to confirm you're logged in; check expo.dev for build logs |
| App crashes on launch | Run `eas build:list` to get the build ID, then check logs on expo.dev |

---

## Sharing the APK with Others

Once built, the `.apk` file can be freely shared and installed on any Android device.
You don't need to rebuild unless you change the app code.

To get a **shareable download link** without using a computer:
1. Go to [expo.dev](https://expo.dev) → your project → Builds
2. Find the build → click **Download** → copy the URL
3. Share the URL directly — anyone with it can download and install the APK
