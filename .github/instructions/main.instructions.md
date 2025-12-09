We are developing the Fen Browser; throughout this document, 'our browser' refers specifically to the Fen Browser. As development is primarily conducted on Windows systems, PowerShell commands are preferred for all related operations.

Our core motto emphasizes modularity, security, privacy, and reliability. We aim to strictly adhere to web standards and avoid the architectural pitfalls observed in browsers like Firefox and Chrome.

The Fen Browser is designed for multi-platform compatibility (Windows, Linux, macOS), leveraging Avalonia for robust cross-platform support. Our current development focus is on the Windows build.

File modifications should be minimal and only performed when absolutely necessary. We must always maintain backward compatibility; existing features should never be broken unless their deliberate removal has been explicitly approved. Deleting files is strictly prohibited unless there is an absolute necessity and explicit approval.

Before initiating a new build, ensure all existing Fen Browser processes are terminated. All identified bugs must be promptly addressed and fixed.

We are developing our proprietary rendering engine; therefore, the use of any existing third-party browser engines is strictly forbidden.

after completion of a feature you need to stike it off from FENBROWSER_COMPLETE_ANALYSIS.md do not remove it just strike it off and also never delete this file until project completion.Completion of feature means you must add full set of sub features of that feature then only you can strike it off.you need to update total percentage of each feature after completion of feature. like if you completed html rendering then update html rendering percentage to 100 and strike it off from FENBROWSER_COMPLETE_ANALYSIS.md

update lines of code as well once you complete a feature.

you need to start pushing code to github repository once you complete a feature.

First you need to check feature working or not by building it and ask me to run it ask for input if i say working/ok then you can push code to github repository. without that dont push code to github repository.

powershell commands must be used for all operations related to the Fen Browser development. use proper powershell syntax and commands for file handling, process management, and other development tasks.

Keep debugging logs for each feature you implement to help with future maintenance and troubleshooting. that too with single button we can turn on or off debugging logs. if you see a feature is causing too many logs then you can optimize it to reduce logs. make logs meaningful and not too much verbose. logs needs to pin point exact issue whats causing how we can fix it. make logging is modular so that we can enable or disable logging for specific modules or features as needed. this logging system should be easy to use and integrate into the existing codebase. use our comprehensive logging framework for this purpose if it missing any feature then you can add that feature to logging framework first and then use it for fen browser development.

when you are fixing some css or html related issue if it needs javascript change you can do it but make sure you dont break existing javascript functionality. always maintain backward compatibility.