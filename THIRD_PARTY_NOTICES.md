# Third-party notices

GameLocalizer source is MIT licensed. Dependencies and downloaded models retain their own licenses.

- Helsinki-NLP OPUS-MT EN→RU: Apache-2.0. Model card: https://huggingface.co/Helsinki-NLP/opus-mt-en-ru
- Xenova ONNX conversion: Apache-2.0 as declared in its model card: https://huggingface.co/Xenova/opus-mt-en-ru
- ONNX Runtime and Microsoft.ML.Tokenizers: MIT; see https://github.com/microsoft/onnxruntime and https://github.com/dotnet/machinelearning
- .NET runtime, WPF and Microsoft.Extensions libraries: MIT; see https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- DirectML native redistributable is governed by Microsoft's package license, not the GameLocalizer MIT license. See https://www.nuget.org/packages/Microsoft.AI.DirectML and its license link.
- Microsoft.Data.Sqlite / SQLitePCLRaw: see their NuGet package licenses (MIT / Apache-2.0 respectively); SQLite itself is public domain.

Model files are downloaded separately from the pinned upstream revision; they are not included in the application ZIP. NuGet retains dependency license metadata. The runtime distribution includes its own license and third-party notices.
