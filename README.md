# Multi-Tool AI Azure Agent

A **.NET-based intelligent agent framework** that connects to multiple tools and APIs — including **Local KPI** and **Weather REST APIs** — to provide real-time insights, contextual automation, and analytics powered by **Azure AI**.

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
    A("Console App") --> B("Azure AI foundry / AI Agent")
    B --> C("Multi-Tool AI Agent (.NET Core)")
    C --> M{"Decide tool based on the user request"}
    M --> D["Local KPI API"] & E["Weather REST API"]
    E --> G["Response Engine"]
    D --> G

