# No-install Windows launcher (PowerShell)

The original approach Claude Switch grew from: PowerShell + VBScript that create a
`Claude (<Name>)` desktop shortcut per profile, each opening Claude with its own
`--user-data-dir`. It has no dashboard, usage or hand-off. **Most people should
install Claude Switch instead.**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\windows\Setup.ps1 -Profile Work,Personal -ReuseDefaultForWork
```

Derived from [claude-desktop-clone](https://github.com/vodongha/claude-desktop-clone)
by vodongha (MIT); see [NOTICE](../../NOTICE).
