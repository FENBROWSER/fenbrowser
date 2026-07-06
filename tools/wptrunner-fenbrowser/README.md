# wptrunner-fenbrowser

FenBrowser product plugin for upstream WPT `wptrunner`.

This package registers the `fenbrowser` product through WPT's documented
`wptrunner.products` entry point instead of patching WPT core.

## Install

Install into the same Python environment used by the target WPT checkout:

```powershell
C:\Users\udayk\Videos\wpt\_venv3\Scripts\python.exe -m pip install -e C:\Users\udayk\Videos\fenbrowser-test\tools\wptrunner-fenbrowser
```

## Run

Pass both the FenBrowser Host binary and the WebDriver launcher explicitly:

```powershell
python C:\Users\udayk\Videos\wpt\wpt run `
  --venv C:\Users\udayk\Videos\wpt\_venv3 `
  --skip-venv-setup `
  --binary C:\Users\udayk\Videos\fenbrowser-test\FenBrowser.Host\bin\Debug\net10.0\FenBrowser.Host.exe `
  --webdriver-binary C:\Users\udayk\Videos\fenbrowser-test\scripts\wpt-webdriver-launcher.cmd `
  fenbrowser /acid/acid2/reftest.html
```

If WPT reports missing host-file configuration, run the `wpt make-hosts-file`
command it prints. Custom products use WPT's normal environment checks.
