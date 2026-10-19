# 第三方材料与构建工具声明

本项目原创代码的 [MIT License](LICENSE) 不覆盖以下第三方材料。第三方材料保留上游许可、版权及作者说明。

## Inno Setup 简体中文语言文件

- 本仓库文件：[`installer-assets/ChineseSimplified.isl`](installer-assets/ChineseSimplified.isl)。
- 直接来源：[Inno Setup 官方源码仓库，`is-6_7_3` 标签](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl)。
- 上游翻译索引：[Inno Setup Translations](https://jrsoftware.org/files/istrans/)。
- 许可文本：随所用上游版本提供的 [Inno Setup License](licenses/Inno-Setup.txt)，并保留文件内原有说明。

该语言文件按上游内容保留，文件头包含维护者 Zhenghan Yang、译文来源及维护地址等信息。本项目不将整个语言文件重新标注为 MIT；文件头所链接的其他翻译仓库，也不改变本项目所取上游文件的来源和许可边界。

## Inno Setup 安装引擎与构建编译器

安装程序由 [Inno Setup](https://jrsoftware.org/) 生成，使用版本为 6.7.3。对应上游源码见 [`is-6_7_3`](https://github.com/jrsoftware/issrc/tree/is-6_7_3)，许可全文保存在 [`licenses/Inno-Setup.txt`](licenses/Inno-Setup.txt)。

```text
Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.
https://jrsoftware.org/
```

生成的安装程序包含 Inno Setup 安装引擎及卸载组件，相关版权、网站地址和许可说明按上游原有内容保留。本项目的 MIT 许可不替代 Inno Setup 的许可。

Inno Setup 编译器是单独准备的构建依赖，不纳入本仓库，也不作为本产品的应用文件安装。获取方式与构建方法见 [README](README.md)，来源记录见 [`installer-assets/来源.txt`](installer-assets/来源.txt)。
