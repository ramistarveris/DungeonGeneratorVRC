# DungeonGeneratorVRC

VRChat Worlds (Unity 2022 / ALCOM) 向けの Dungeon Generator。

## 使い方
1. ALCOM で **VRChat Worlds** プロジェクトを作る。UdonSharp が導入されていることを確認する。
2. このリポジトリの **Assets/DungeonGenerator** を、既存プロジェクトの Assets フォルダにコピーする。
3. Unity メニュー **Tools > Dungeon Generator > Create Component Controller** から設定オブジェクトを作る。
4. Inspector で Floor (1–7)、Seed（整数 / 文字列 / `random`）、Dungeon Mode、Baked Variants などを設定する。
5. **Generate Dungeon (Editor)** を押し、シーンを保存する。
6. VRChat SDK の Build & Test で、ワールド内の `GENERATE NEXT` ボタンを試す。

## 生成方式
- **Editor**: Unity 上で床・壁・通路・Start 部屋のスポーン位置を生成し、Scene に保存。
- **World runtime**: UdonSharp で事前生成した複数のマップを切り替える。選択インデックスをネットワーク同期。
- **Dungeon Map Mode**: Editor で平面マップをプレビュー。
- **Dungeon Mode**: 3D ダンジョンを焼き込み、VRChat 標準プレイヤーで歩ける地形にする。

### 制約
VRChat 内で任意の新規 Seed を使って無限生成する実装ではありません。**あらかじめ焼いた N 種類のマップを切り替える方式**です。Unity/VRChat 上の実動作・複数人同期は未検証です。

旧Unity 6版: https://github.com/ramistarveris/Dungeon-Generator

## 開発メモ
`Assets/DungeonGenerator/Editor/` は Editor 専用。`Runtime/` は UdonSharp と VRChat SDK に依存します。既存の VRChat プロジェクトを丸ごと置き換えず、必要なアセットだけ追加してください。
