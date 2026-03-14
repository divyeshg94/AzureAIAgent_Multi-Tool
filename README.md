# Multi-Tool AI Azure Agent

A **.NET 8 multimodal AI agent** that intelligently connects to multiple tools — KPI lookup, Weather API, Image generation, Audio narration, and Video generation — powered by Azure AI Foundry (GPT-5, GPT-Image-1-Mini, GPT-Audio-Mini, Sora-style video).

This agent understands natural language, determines which tool to invoke, combines results, and produces unified outputs, including text, images, audio, and videos.

---
**Capabilities**

🧠 1. Reasoning & Orchestration (GPT-5)
- Natural language understanding
- Dynamic tool selection
- Multi-step, multi-tool reasoning
- TL;DR summaries and structured output


📊 2. KPI Intelligence (C# Tool)
- Quickly returns definitions for KPIs (MRR, NPS, etc.)
- Lightweight and local for instant response

🌦️ 3. Real-Time Weather (REST Tool)
- Uses the Open-Meteo geocoding + forecast API
- Returns live temperature (C/F) for any location
- Fully stateless and latency-optimized

🖼️ 4. Image Generation (GPT-Image-1-Mini)
- Creates visuals based on user prompts
- Ideal for dashboards, summaries, and thumbnails

🔊 5. Audio Narration (GPT-Audio-Mini)
- Converts summaries to high-quality speech WAV files
- Great for reports, voice assistants, and daily briefings

🎬 6. Video Generation (Sora-style Endpoint)
- Generates MP4 videos from prompts
- Produces dynamic, short clips (e.g., 3–10 seconds)
- Automatic HTML “playground” viewer included

---

## Features

- 🤖 **AI Agent Core**  
  Built on Azure OpenAI for intelligent orchestration, contextual reasoning, and data-driven decision-making.

- 📊 **Local KPI Integration**  
  Fetches and analyzes local business Key Performance Indicators via REST APIs.

- 🌦️ **Weather Intelligence**  
  Integrates with third-party Weather REST APIs to blend environmental data with operational insights.

- ☁️ **Azure-Native Design**  
  Runs seamlessly on Azure App Service, Functions, or Container Apps.

- 🧩 **Extensible Architecture**  
  Easily add new APIs or data sources — the modular design supports pluggable connectors.

- 🔒 **Secure and Scalable**  
  Follows Azure security best practices for authentication, logging, and monitoring.

---

## 🏗️ Architecture Overview

```mermaid
---
title: Multi-Tool AI Azure Agent
config:
  theme: redux
  layout: dagre
---
flowchart TD
    A("Console App") --> B("Multi-Tool AI Agent (.NET Core)")
    B --> C("Azure AI foundry / AI Agent  (GPT-5)")
    C --> M{"Decide tool based on the user request"}
    M --> D("KPI Tool 
    (C#)") & E("Weather Tool 
    (REST API)") & F("Image Tool 
    (GPT-Image-1-Mini)") & H("Audio Tool
    (GPT-Audio-Mini)") & N("Video Tool
    (Sora)")
    E --> G["Response Engine"]
    D --> G
    F --> G
    H --> G
    N --> G
    G --> I("Text Output") & J("Image Output") & K("Audio Output") & L("Video Output")

```
---

## Security architecture

This repo demonstrates the three security pillars from the blog post
[AI agents need security engineering, not just guardrails](#).

### 1. Managed Identity (no secrets in code)

The agent authenticates to Azure using `DefaultAzureCredential`.
Locally, this resolves via `az login`. In Azure, it uses the resource's
Managed Identity.

Required role assignments:
| Resource | Role |
|---|---|
| Azure AI Foundry project | Azure AI User |
| Azure Blob Storage (if used) | Storage Blob Data Reader |
| Azure AI Search (if used) | Search Index Data Reader |

**No connection strings. No API keys. No secrets in appsettings.json.**

### 2. Input validation (Security/InputGuard.cs)

Every user message passes through `InputGuard.Validate()` before
reaching the model. This checks for:
- Obvious prompt injection patterns ("ignore previous instructions", etc.)
- Out-of-domain inputs (anything unrelated to weather, KPIs, or media)
- Excessive length

Retrieved content (from tools or documents) is wrapped in explicit
delimiters using `InputGuard.WrapRetrievedContent()` to separate it
from trusted instructions in the prompt.

### 3. Audit trail (Security/AgentTelemetry.cs)

Every tool call is traced with OpenTelemetry and shipped to Azure
Application Insights (when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set).
In local development, traces are written to the console.

You can answer these questions for any request:
- Which tool was called and with what input?
- How long did it take?
- What did it return?

### Required environment variables

| Variable | Purpose |
|---|---|
| `AZURE_AI_PROJECT_ENDPOINT` | Your Azure AI Foundry project endpoint |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Azure Monitor (optional locally) |

Set these in your local environment or in Azure App Settings.
Do not put them in `appsettings.json` or `.env` files committed to Git.

---

**Setup & Requirements**
**Prerequisites**

1. .NET 8 SDK
1. Azure AI Foundry Project (or Azure OpenAI Resource)
1. Deployed models:
- GPT-5
- GPT-Image-1-mini
- GPT-Audio-mini
- Sora (preview)

---

**Why This Project Exists**

1. This repository demonstrates:
1. Multi-tool agent patterns
1. Tool orchestration
1. Real-time REST integration
1. Multimodal generation across text, audio, image, and video
1. .NET best practices for AI workloads
1. Azure-native authentication and design

---

## Try these prompts (see multi-tool routing)

- What's the temperature in Herndon in F?
- Weather check for Chennai in c
- Compare MRR and NPS, and also tell me the current temperature in Dublin.
- Define NPS and give me the weather in Seattle.

---

## Sample: 
<img width="800" height="410" alt="image" src="https://github.com/user-attachments/assets/3547004a-b673-4daf-82bd-2d7f6a3206cb" />


#2:
Prompt:
check current weather in Herndon, and create a video illustrating different tech professionals walking with their workstation

<img width="1100" height="249" alt="image" src="https://github.com/user-attachments/assets/959c711d-90ab-490f-9f3d-7b70a269503a" />

https://youtube.com/shorts/3x2gFnbPzLg?si=qFRm3IzEpoJ_sEXh
---
