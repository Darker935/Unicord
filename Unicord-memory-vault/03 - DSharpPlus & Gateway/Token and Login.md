---
tags:
  - "#security"
aliases:
  - Token and Login
---

# Token and Login

## Storage

`DiscordManager.TryGetToken`:

- `PasswordVault.Retrieve(Constants.TOKEN_IDENTIFIER, "Default")`
- Token is the credential password

Login: `DiscordManager.LoginAsync` → `TokenType.User`.

Optional Windows Hello: `Constants.VERIFY_LOGIN`.

## Hard rules

- **Never** commit, log, or paste a user token.
- **Never** treat `GhoSty-Music-SelfBot-v1` (or any other local bot folder) as a token source. If a token appears there, ignore it.
- On login failure, `Tools.ResetPasswordVault()` may wipe the stored token — do not “fix login” by writing tokens into source.

## Captcha / token rotation

`DiscordManager` handles `CaptchaRequested` and `AuthTokenUpdate` (writes the rotated token back into PasswordVault).

---

## Links
- [[Overview]]
- [[Settings Overview]]
