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

配布用 (Native AOT, 自己完結型)。VS Build Tools の C++ ワークロード（リンカ）が必要:

```powershell
scripts\publish.ps1
```

## 操作

| 操作 | 動作 |
|---|---|
| クリック / ダブルクリック | 一時停止 / 全画面 |
| ドラッグ | ウィンドウ移動（動画上のどこでも） |
| ホイール | 音量（横ホイールでシーク） |
| 右クリック | コンテキストメニュー |
| キーボード | mpv の既定バインドと `input.conf` がそのまま効く（Space, ←→, 9/0, m, f, q など） |
| Ctrl+O / Ctrl+Shift+S / Ctrl+Shift+P / Ctrl+Shift+M / Ctrl+, / F11 | 開く / クイック設定 / プレイリスト / ミニプレイヤー / 環境設定 / 全画面 |
| `Nami.exe --register` | エクスプローラーの「プログラムから開く」と既定のアプリに登録（HKCU。`--unregister` で解除） |

## 構成

| パス | 役割 |
|---|---|
| `src/Nami/Mpv/LibMpv.cs` | libmpv の P/Invoke（client.h と 1:1） |
| `src/Nami/Mpv/MpvPlayer.cs` | mpv コアのラッパー。イベントスレッド、プロパティ監視、UI スレッドへのディスパッチ |
| `src/Nami/Controls/VideoView.cs` | SwapChainPanel 派生。プレイヤー生成、スワップチェーン貼り付け、サイズ/DPI 追従 |
| `src/Nami/Interop/SwapChainPanelInterop.cs` | ISwapChainPanelNative と IDXGISwapChain2 の呼び出し |
| `src/Nami/Player/PlayerViewModel.cs` | mpv プロパティを UI 向け状態に写像。コマンドもここ |
| `src/Nami/Controls/Osc.xaml` | IINA 風フローティング OSC |
| `src/Nami/Controls/Sidebar.xaml` | クイック設定（映像/音声/字幕）とプレイリスト/チャプターのサイドバー |
| `src/Nami/MainPage.xaml` | 動画面。ポインタ/キー入力、OSC の自動非表示、ドラッグ&ドロップ |
| `src/Nami/MainWindow.xaml` | 透明タイトルバー、全画面、動画サイズへのフィット、ミニプレイヤー |
| `src/Nami/Interop/AspectRatioLock.cs` | WM_SIZING でウィンドウのアスペクト比を動画に固定 |
| `src/Nami/Interop/DisplayInfo.cs` | DXGI でモニターの HDR 状態・輝度・SDR 白レベルを取得（`Player/HdrController.cs` が mpv に渡す） |
| `src/Nami/Services/FileAssociation.cs` | HKCU へのメディアアプリ登録 |
| `third_party/libmpv/` | libmpv のヘッダと DLL（DLL は git 管理外） |

mpv の設定ファイルは `%LOCALAPPDATA%\Nami\mpv\` （`mpv.conf`, `input.conf` など）から読む。ログは同じ場所の `mpv.log`。
