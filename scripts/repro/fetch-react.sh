#!/usr/bin/env bash
# Downloads the React builds the repro pages need. They are third-party artifacts,
# so they stay out of git; run this once before serving the pages.
set -e
cd "$(dirname "$0")"
curl -sL -o react.js            "https://unpkg.com/react@18.3.1/umd/react.development.js"
curl -sL -o react-dom.js        "https://unpkg.com/react-dom@18.3.1/umd/react-dom.development.js"
curl -sL -o react19.mjs         "https://esm.sh/react@19.0.0/es2022/react.mjs"
curl -sL -o react19-dom.core.mjs "https://esm.sh/react-dom@19.0.0/es2022/client.bundle.mjs"
# esm.sh emits an absolute import for react; point it at the local copy.
sed -i 's#from"/react@19.0.0/es2022/react.mjs"#from"./react19.mjs"#g' react19-dom.core.mjs
echo "ready - serve with: python -m http.server 8731"
