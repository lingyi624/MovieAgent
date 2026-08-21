AI智能电影管家MovieAgent

<img width="2560" height="1440" alt="b1dea0f808c8e2d5599de0eea8381d55" src="https://github.com/user-attachments/assets/1b325ac8-5f74-406f-b53d-a8f0f6d3687c" />
<img width="2560" height="1440" alt="51fdee07551507f8d23b7d4247ee6580" src="https://github.com/user-attachments/assets/d3500211-9e37-44f7-93fe-9ddfdf27b2cf" />
<img width="2560" height="1440" alt="03fee55375fbad08d8802ffc91a3c702" src="https://github.com/user-attachments/assets/2b8dad4f-43d4-452f-9939-e83f1c878d30" />
<img width="2560" height="1440" alt="21078ffe3033545da053109af3f07dec" src="https://github.com/user-attachments/assets/21bd5cd8-4ba4-47b3-9675-c84735eb5166" />
<img width="2560" height="1440" alt="ecedd16b35032fabf16fc16637d9281f" src="https://github.com/user-attachments/assets/257c8f04-bb6e-4f90-88aa-3da43d0969bc" />
<img width="2560" height="1440" alt="feedff9d392cb416e0e74a2195572e60" src="https://github.com/user-attachments/assets/b244eaa8-875a-4078-ae94-106f495bab24" /> 
<img width="2560" height="1440" alt="a90b958235f639e3b2e227448203c7a6" src="https://github.com/user-attachments/assets/ba2065d2-0e68-4b72-a7bc-fc0314ae6d74" />
<img width="2560" height="1440" alt="5771554f5254b619fb08177be9721943" src="https://github.com/user-attachments/assets/2afa56ba-4708-4723-b4d1-c23c6a60e44f" />
<img width="2560" height="1440" alt="4218351fa5fcc0d075e243a108495211" src="https://github.com/user-attachments/assets/5e044a17-7e2d-469a-9521-dfd0d3c72372" />
<img width="2560" height="1440" alt="64b6c0b10f87aebe65355c002cc232eb" src="https://github.com/user-attachments/assets/a2780c7a-7592-49a7-89db-73550893ba65" />
<img width="2560" height="1440" alt="24d945a57f2369b379f6f20712215ad3" src="https://github.com/user-attachments/assets/297ea46b-afa2-46a6-ae02-5948e2334e14" />
<img width="2560" height="1440" alt="80617635cd82b5523c4d2ff1acca0278" src="https://github.com/user-attachments/assets/8a5edfcf-0b5d-4699-a0f2-6b4e7c14f4a9" />
<img width="2560" height="1440" alt="7f8502dfa06a0e7444fbce0a5dc5acc3" src="https://github.com/user-attachments/assets/040e576f-2459-479d-ab25-80e9f8f92e86" />

# 🎬 Movie Agent

**Movie Agent** 是一款面向电影发烧友的 **AI 驱动的本地影视管理工具**。它将智能对话、电影管理、高清播放能力整合于一个桌面应用中，支持纯本地部署，保护你的数据隐私。

## ✨ 核心特性

- 🤖 **AI 智能管家**：基于本地大模型 (Ollama + Llama 3.2)，通过自然语言推荐电影、控制播放、语义搜索（例如：“找一部让人哭得很爽的科幻片”）。
- 🗄️ **智能电影库**：支持扫描 NAS 多磁盘 (`\\192.168.1.11\disk`)，自动解析文件名（中文/英文/年份），并从 TMDB 抓取元数据。
- 🔍 **语义检索**：结合 `LanceDB` 向量数据库，实现基于语义而非关键词的深度搜索。
- ▶️ **专业播放**：无缝调用外部 PotPlayer 或内嵌 LibVLC，完美支持 4K、蓝光原盘、杜比视界及 NAS 网络路径播放。
- 🔒 **隐私优先**：所有 AI 对话、向量数据完全本地处理，无需联网，数据不离开你的设备。

## 🛠️ 技术栈

| 类别 | 技术选型 |
| :--- | :--- |
| **框架** | .NET 10, WPF (宿主), Blazor Hybrid (UI) |
| **AI 核心** | Ollama (LLaMA 3.2, Nomic-Embed-Text) |
| **向量数据库** | LanceDB |
| **元数据** | TMDB API |
| **播放引擎** | LibVLCSharp / PotPlayer (外部) |
| **数据存储** | SQLite + EF Core |

## 🚀 快速开始

### 环境准备

1.  **安装 .NET 10 SDK**
    [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)

2.  **安装并启动 Ollama**
    ```bash
    # 下载安装 https://ollama.com
    ollama serve
    # 拉取模型 (对话模型 + 嵌入模型)
    ollama pull llama3.2
    ollama pull nomic-embed-text

    git clone https://github.com/lingyi624/MovieAgent.git
cd MovieAgent
dotnet restore
dotnet run --project src/MovieAgent.Host

首次配置
在 appsettings.json 中配置你的 NAS 共享路径（如有）。

默认 Ollama 服务地址为 http://localhost:11434，无需更改。

🎯 使用示例
对话推荐：在 AI 对话框中输入“推荐一部诺兰导演的高分烧脑片”。

语义搜索：搜索“关于梦境与现实的哲学思考”。

智能播放：直接说“播放盗梦空间”。
MovieAgent/
├── src/
│   ├── MovieAgent.Host/          # WPF 宿主 & 视频播放窗口
│   ├── MovieAgent.Core/          # 核心业务、AI 服务、实体模型
│   ├── MovieAgent.Blazor/        # Blazor 前端组件 (对话、海报墙)
│   └── MovieAgent.Infrastructure/# LanceDB、文件扫描基础设施
└── tests/                        # 单元测试
贡献
欢迎通过 Issue 或 Pull Request 贡献代码。

📄 许可证
本项目采用 LGPL2.1 开源协议。

⚠️ 免责声明
本项目仅供个人学习与交流使用。所有媒体文件的版权归其原始权利人所有。使用本工具时，请确保你拥有播放该媒体的合法权利。
