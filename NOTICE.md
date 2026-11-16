# Avisos de terceiros

## Win11Debloat

O catálogo de apps em `src/Vapt.Core/Debloat/apps.json` (IDs, nomes, descrições em português e recomendações) foi gerado a partir de `Config/Apps.json` e `Config/Languages/pt-BR/Apps.json` do [Win11Debloat](https://github.com/Raphire/Win11Debloat). Os apps marcados como "unsafe" lá (Loja, Edge, Terminal, componentes do Xbox) foram retirados.

```
MIT License

Copyright (c) 2020 Raphire (https://github.com/Raphire)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

```

## Bibliotecas da seção Desempenho

- **[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)** (MPL-2.0): leitura dos sensores. Usada sem modificação, como pacote NuGet; o código-fonte está no link.
- **[Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview)** (MIT): eventos ETW para medir o FPS.
- **[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)** (MIT): Direct3D 11 do benchmark 3D e do stress de GPU.
- **PawnIO** (driver opcional, instalado pelo winget a pedido do usuário): acesso aos sensores do processador.
