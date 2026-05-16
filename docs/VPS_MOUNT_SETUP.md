# VPS Mount Setup Guide

## Part 1: Windows VPS Configuration (via RDP)

### Step 1: Connect to VPS
```
RDP to: 104.245.107.193
```

### Step 2: Share the NinjaTrader Strategies Folder

1. **Open File Explorer**, navigate to:
   ```
   C:\Users\[your_username]\Documents\NinjaTrader 8\bin\Custom\Strategies
   ```

2. **Right-click on "Strategies" folder** → Properties → Sharing tab

3. **Click "Advanced Sharing"**
   - ☑ Check "Share this folder"
   - Share name: `NT8Strategies` (or your preferred name)
   - Click "Permissions"

4. **Set Permissions**:
   - Add your Windows user
   - Grant "Full Control" (or at minimum "Change" permission)
   - Click OK

5. **Note the share path**:
   ```
   \\104.245.107.193\NT8Strategies
   ```

### Step 3: Create a Windows User for Mount (if needed)

If you want a dedicated user for this mount:

1. **Computer Management** → Local Users and Groups → Users
2. **Right-click** → New User
3. Create user: `nt8share` (example)
4. Set a strong password
5. Uncheck "User must change password at next logon"
6. Check "Password never expires"
7. **Add this user to the share permissions** (from Step 2)

### Step 4: Note Your Credentials

You'll need:
```
VPS IP: 104.245.107.193
Share name: NT8Strategies
Username: [your Windows username or nt8share]
Password: [your password]
Domain: (usually blank for local VPS)
```

---

## Part 2: Linux Machine Configuration (Run these commands)

### Step 1: Install CIFS utilities

```bash
sudo apt-get update
sudo apt-get install cifs-utils
```

### Step 2: Create mount point

```bash
sudo mkdir -p /mnt/vps_nt8_strategies
sudo chown bernard:bernard /mnt/vps_nt8_strategies
```

### Step 3: Create credentials file (secure)

```bash
sudo nano /root/.vps_nt8_credentials
```

Add this content (replace with your actual credentials):
```
username=YOUR_WINDOWS_USERNAME
password=YOUR_WINDOWS_PASSWORD
domain=
```

Save and secure it:
```bash
sudo chmod 600 /root/.vps_nt8_credentials
```

### Step 4: Test manual mount

```bash
sudo mount -t cifs //104.245.107.193/NT8Strategies /mnt/vps_nt8_strategies -o credentials=/root/.vps_nt8_credentials,uid=bernard,gid=bernard,file_mode=0775,dir_mode=0775
```

### Step 5: Verify mount works

```bash
ls -lh /mnt/vps_nt8_strategies/
```

You should see your existing NinjaTrader strategy files.

### Step 6: Make mount permanent (optional)

Add to `/etc/fstab`:
```bash
sudo nano /etc/fstab
```

Add this line at the bottom:
```
//104.245.107.193/NT8Strategies /mnt/vps_nt8_strategies cifs credentials=/root/.vps_nt8_credentials,uid=bernard,gid=bernard,file_mode=0775,dir_mode=0775,nofail 0 0
```

Save and test:
```bash
sudo mount -a
```

---

## Part 3: Deploy Flagship v1.1

Once mounted, this command will work:

```bash
sudo cp /home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs /mnt/vps_nt8_strategies/
```

Verify:
```bash
ls -lh /mnt/vps_nt8_strategies/CG_MNQ_Flagship_Hybrid_v1_*.cs
```

Then in NinjaTrader 8 on VPS:
```
Tools → Compile
```

---

## Troubleshooting

### "Permission denied" when mounting

**Check**:
- Windows share permissions are correct
- Credentials file has correct username/password
- Windows Firewall allows SMB (port 445)

**Fix**:
```bash
# Test with explicit credentials
sudo mount -t cifs //104.245.107.193/NT8Strategies /mnt/vps_nt8_strategies -o username=YOUR_USER,password=YOUR_PASS,uid=bernard,gid=bernard
```

### "Host is down" or "Connection refused"

**Check**:
- VPS is running (can you RDP to it?)
- VPS IP is correct (104.245.107.193)
- Windows Firewall allows File and Printer Sharing

**On Windows VPS**:
1. Control Panel → System and Security → Windows Defender Firewall
2. Allow an app → File and Printer Sharing (check Private and Public)

### "No such file or directory"

**Check**:
- Share name is exactly correct (case-sensitive)
- Path on Windows exists
- Share is actually shared (check Windows → Manage → Computer Management → Shared Folders)

### Mount works but files don't appear

**Check**:
- User has read permissions on Windows folder
- Files aren't hidden
- Windows folder path is correct

---

## Alternative: Quick One-Time Copy (No Mount)

If you just want to copy v1.1 right now without setting up the mount:

### Option A: Use smbclient (if VPS allows)

```bash
# Install smbclient
sudo apt-get install smbclient

# Copy file
smbclient //104.245.107.193/NT8Strategies -U YOUR_USERNAME -c "put /home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs"
```

### Option B: Use your local Windows machine as intermediary

1. **Copy to local Windows machine** (via SSH/SCP from Linux to your local machine)
2. **RDP to VPS** with local drives enabled
3. **Copy from local drive to VPS NinjaTrader folder**

---

## Security Notes

- Store credentials securely in `/root/.vps_nt8_credentials` with 600 permissions
- Consider using a dedicated user account for the share (not your main Windows admin)
- If VPS has public IP, ensure Windows Firewall is configured properly
- Consider VPN if connecting over public internet

---

**Once mounted, future deployments are simple**:
```bash
sudo cp ~/trading4/CG_MNQ_MarketReplayLab/ninjascript/[filename].cs /mnt/vps_nt8_strategies/
```
