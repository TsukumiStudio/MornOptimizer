# MornOptimizer

## 概要

Unityプロジェクトのビルドサイズを削減するためのエディタツール。パッケージ・asmdef・テクスチャ・アセット・ビルド設定を6つのタブで最適化する。

## 導入方法

Unity Package Manager で以下の Git URL を追加:

```
https://github.com/TsukumiStudio/MornOptimizer.git
```

`Window > Package Manager > + > Add package from git URL...` に貼り付けてください。

## 依存関係

| 種別 | 名前 |
|------|------|
| Mornライブラリ | なし |

## 使い方

`Tools > MornOptimizer` でウィンドウを開く。

### パッケージタブ

未使用パッケージを検出・削除する。

- `packages-lock.json` から全インストール済みパッケージを解析
- DLL参照・型リフレクション・Scene/Prefabコンポーネント・ファイル拡張子の5層で使用判定
- パッケージ間のasmdef参照・プリコンパイルDLL参照も検出し、他パッケージから参照されているものは使用中と判定
- Featureパッケージ（`com.unity.feature.2d` 等）をサブパッケージに展開して個別判定
- パッケージ間依存を `← パッケージ名` で可視化
- 未使用パッケージを選択すると、連鎖で不要になるパッケージにもチェックボックスが出現（1段階ずつ）

### Asmdef詳細タブ

パッケージ内のasmdef単位で使用状況を解析し、未使用アセンブリを削除する。

- 複数asmdefを持つパッケージの個別解析
- Assets/コード・Scene/Prefab・パッケージ間参照（CompilationPipelineベース）の3層で使用判定
- 未使用asmdefをチェックして「カスタムパッケージ化して削除」で実行
- パッケージを`CustomPackages/`にコピーし、選択したasmdefのディレクトリを削除
- 削除後、`package.json`の不要な依存を自動クリーンアップ
- 連鎖で不要になるasmdefにもチェックボックスが出現

### テクスチャタブ

テクスチャのインポート設定を解析し、過大なMaxSize・圧縮なし・不要なミップマップを検出。一括修正ボタンで推奨設定に変更する。

### 余白トリムタブ

PNG画像の透明余白を自動検出し、トリミング・4の倍数アライメントを行う。推定ファイルサイズ削減量を表示する。

### 未参照アセットタブ

`Assets/_Develop/` 内でどのシーン・Prefab・ScriptableObjectからも参照されていないアセットを検出。型ごとにグループ化し、ファイルサイズ付きで表示する。個別に選択・削除が可能。

※ `Resources.Load()` や Addressables 経由の参照は検出できない。

### ビルド設定タブ

Scripting Backend・Managed Stripping Level・Development Build・WebGL圧縮の推奨値を表示。個別修正・一括修正ボタンで推奨設定に変更する。

## 注意事項

- パッケージ削除やasmdefカスタム化は**元に戻せない操作**です。実行前に必ずgitコミットまたはバックアップを作成してください。
- `CustomPackages/` にカスタムパッケージ化したパッケージは、パッケージマネージャーの自動更新対象外になります。
