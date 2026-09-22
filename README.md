# Nami

IINA 風の、Windows ネイティブな mpv フロントエンド。WinUI 3 (Windows App SDK) + libmpv。

## 仕組み

- libmpv を `--vo=gpu-next --gpu-context=d3d11 --d3d11-output-mode=composition` で起動する。
  mpv はウィンドウを作らず DXGI コンポジション用スワップチェーンだけを作る（mpv 0.41+）。
- `display-swapchain` プロパティで受け取った `IDXGISwapChain*` を XAML の `SwapChainPanel` に貼る。
  描画・提示・hwdec・シェーダは全部 mpv 側。UI は XAML でその上に重ねる。
- リサイズは `d3d11-composition-size` プロパティを物理ピクセルで更新するだけ。

## ビルド

前提: .NET 10 SDK、Windows 10 1809 以降。Visual Studio は不要。

```powershell
scripts\fetch-libmpv.ps1          # third_party/libmpv/libmpv-2.dll を取得（7-Zip が必要）
dotnet build src\Nami -c Debug -p:Platform=x64
dotnet run --project src\Nami -- "C:\path\to\video.mkv"
```

配布用 (Native AOT, 自己完結型):

```powershell
dotnet publish src\Nami -c Release -p:Platform=x64
```

## 構成

| パス | 役割 |
|---|---|
| `src/Nami/Mpv/LibMpv.cs` | libmpv の P/Invoke（client.h と 1:1） |
| `src/Nami/Mpv/MpvPlayer.cs` | mpv コアのラッパー。イベントスレッド、プロパティ監視、UI スレッドへのディスパッチ |
| `src/Nami/Controls/VideoView.cs` | SwapChainPanel 派生。プレイヤー生成、スワップチェーン貼り付け、サイズ/DPI 追従 |
| `src/Nami/Interop/SwapChainPanelInterop.cs` | ISwapChainPanelNative と IDXGISwapChain2 の呼び出し |
| `src/Nami/MainPage.xaml` | 画面。今は仮のトランスポートバー |
| `third_party/libmpv/` | libmpv のヘッダと DLL（DLL は git 管理外） |

mpv の設定ファイルは `%LOCALAPPDATA%\Nami\mpv\` （`mpv.conf`, `input.conf` など）から読む。ログは同じ場所の `mpv.log`。
