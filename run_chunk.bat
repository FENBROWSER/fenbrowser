@echo off
dotnet run --project FenBrowser.Test262Benchmark/FenBrowser.Test262Benchmark.csproj -c Release -- run_chunk %1 100
