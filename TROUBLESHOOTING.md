# Troubleshooting Infinite Redirect Loop

If you're experiencing an infinite redirect loop after deploying, follow these steps:

## Quick Fixes

### 1. Clear Browser Data

The most common cause is stale cookies from a previous deployment.

**Chrome/Edge:**
1. Press F12 to open DevTools
2. Right-click the refresh button
3. Select "Empty Cache and Hard Reload"
4. Or go to `chrome://settings/clearBrowserData`
5. Select "Cookies and other site data"
6. Clear data for the last hour

**Firefox:**
1. Press F12 to open DevTools
2. Go to Storage tab
3. Expand Cookies
4. Right-click on your domain
5. Select "Delete All"

**Or just try Incognito/Private browsing mode.**

### 2. Verify the Application is Running

Check that the application started successfully:

```bash
# Check the logs for these messages:
# - "Admin user 'admin' created successfully"
# - "Database initialized with 1 user(s)"
# - "Now listening on: https://0.0.0.0:5001"
```

### 3. Test Authentication Manually

Open your browser's DevTools (F12) and try to login while watching:

1. **Network Tab**: Check if the login POST request succeeds (status 200 or 302)
2. **Application/Storage Tab**: Check if cookies are being set
   - Look for a cookie named `VibeSwarm.Auth`
   - It should have `HttpOnly` and `SameSite=Lax` flags

### 4. Check for Mixed HTTP/HTTPS

Make sure you're accessing the application via HTTPS:
- ✅ `https://your-ip:5001` (correct)
- ❌ `http://your-ip:5000` (might cause issues)

The application will redirect HTTP to HTTPS, but if you're bookmarking the HTTP URL, you might get stuck in a loop.

## Diagnostic Steps

### Step 1: Check if Cookies Are Being Set

1. Open DevTools (F12)
2. Go to Network tab
3. Navigate to `https://your-ip:5001/login`
4. Enter credentials and click Login
5. Look at the POST request to `/login`
6. Check the Response Headers for `Set-Cookie`

**You should see:**
```
Set-Cookie: VibeSwarm.Auth=...; path=/; samesite=lax; httponly
```

If you don't see this cookie being set, the issue is with the server.

### Step 2: Check if Cookies Are Being Sent Back

1. After login attempt, look at the GET request to `/` (home page)
2. Check the Request Headers for `Cookie`

**You should see:**
```
Cookie: VibeSwarm.Auth=...
```

If the cookie is set but not sent back, it might be a browser security policy issue.

### Step 3: Check Server Logs

Look for these in your server logs:

**Good signs:**
```
info: Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler[7]
      Ticket validated
info: Microsoft.AspNetCore.Authorization.DefaultAuthorizationService[1]
      Authorization was successful
```

**Bad signs:**
```
info: Microsoft.AspNetCore.Authorization.DefaultAuthorizationService[2]
      Authorization failed
warn: Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler[8]
      Ticket expired
```

### Step 4: Verify User Exists in Database

On your server, check the database:

```bash
# If using SQLite
sqlite3 vibeswarm.db "SELECT UserName, Email FROM AspNetUsers;"
```

You should see your admin user.

## Common Issues and Solutions

### Issue 1: Cookie Not Set Due to SameSite Policy

**Symptom**: Cookie is not set in browser

**Solution**: Already fixed in the latest version. The cookie uses `SameSite=Lax` which should work.

### Issue 2: Clock Skew Between Server and Client

**Symptom**: Cookie expires immediately

**Solution**: Verify server time is correct:
```bash
date
# If wrong, sync time:
sudo timedatectl set-ntp true
```

### Issue 3: Application Running in HTTP-Only Mode

**Symptom**: Redirect loop even though accessing HTTPS

**Solution**: Verify Kestrel is configured for HTTPS:
```bash
# Check logs for:
# "Now listening on: https://0.0.0.0:5001"
# "Now listening on: http://0.0.0.0:5000"
```

### Issue 4: Database Connection Issues

**Symptom**: No admin user created

**Solution**:
1. Check database file exists and has correct permissions
2. Look for migration errors in logs
3. Try deleting `vibeswarm.db` and restarting (creates fresh database)

### Issue 5: Blazor Circuit Not Authenticating

**Symptom**: Login succeeds but immediately redirects back to login

**Solution**: This version includes `RevalidatingIdentityAuthenticationStateProvider` which should fix this. Make sure you:
1. Deployed the latest code
2. Restarted the application
3. Cleared browser cookies

## Manual Testing

Try this curl command to test authentication:

```bash
# 1. Get login page and extract anti-forgery token
curl -c cookies.txt -b cookies.txt https://your-ip:5001/login -k

# 2. Login (you'll need to extract the verification token from step 1)
curl -c cookies.txt -b cookies.txt -X POST https://your-ip:5001/login \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "Username=admin&Password=YourPassword&RememberMe=false" -k

# 3. Access protected page
curl -b cookies.txt https://your-ip:5001/ -k

# If authentication works, step 3 should return HTML, not redirect
```

## Still Not Working?

If you've tried all of the above and still have issues:

1. **Check browser console** for JavaScript errors
2. **Check Network tab** for failed requests
3. **Review server logs** for exceptions
4. **Try a different browser** to rule out browser-specific issues
5. **Restart the application** after clearing cookies

### Enable Detailed Logging

Set environment variable:
```bash
export ASPNETCORE_ENVIRONMENT=Development
```

This will show detailed error pages and more verbose logging.

### Create a Minimal Reproduction

Try accessing just the login page without any parameters:
```
https://your-ip:5001/login
```

If even this redirects, there's a deeper issue with the authentication configuration.
