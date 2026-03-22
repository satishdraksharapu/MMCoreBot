# 💰 BudgetBot — WhatsApp Budget Tracking Agent

A conversational AI budget assistant powered by **Claude AI**, built with **ASP.NET Core 8**, and deployable to **Railway** for free.

---

## How It Works

```
You (WhatsApp)
    │  "spent ₹500 on groceries"
    ▼
Twilio (WhatsApp API)
    │  POST /api/webhook/whatsapp
    ▼
Your ASP.NET Core Server (Railway)
    │
    ├─► BudgetService  ──► SQLite DB  (read current state)
    │
    ├─► ClaudeService  ──► Claude API
    │         │   Claude calls tool: add_transaction
    │         ▼
    │   BudgetService  ──► SQLite DB  (write transaction)
    │         │
    │         └─► Claude generates friendly reply
    ▼
Twilio  ──►  You: "₹500 added ✅ Remaining: ₹24,500"
```

---

## Prerequisites

| What | Where to get it | Free? |
|------|----------------|-------|
| Claude API Key | https://console.anthropic.com | Yes (free credits) |
| Twilio Account | https://twilio.com | Yes (trial) |
| Railway Account | https://railway.app | Yes |
| GitHub Account | https://github.com | Yes |
| .NET 8 SDK | https://dot.net | Yes |

---

## Step 1 — Get Your API Keys

### Claude API Key
1. Go to https://console.anthropic.com
2. Click **API Keys** → **Create Key**
3. Copy and save it somewhere safe

### Twilio WhatsApp Setup
1. Sign up at https://twilio.com
2. Go to **Messaging** → **Try it out** → **Send a WhatsApp message**
3. Follow Twilio's sandbox setup (you'll send a join code from your WhatsApp)
4. Note your **Twilio Account SID** and **Auth Token** from the dashboard

---

## Step 2 — Run Locally (Optional Testing)

```bash
# Clone / open this project
cd BudgetAgent

# Set your API key temporarily
export Claude__ApiKey="sk-ant-XXXXXXXXXX"

# Run the app
dotnet run
```

The server starts on http://localhost:8080

Use **ngrok** to expose it for Twilio testing:
```bash
ngrok http 8080
# Copy the https://xxxx.ngrok.io URL
```

In Twilio Console → WhatsApp Sandbox → set the webhook to:
```
https://xxxx.ngrok.io/api/webhook/whatsapp
```

---

## Step 3 — Deploy to Railway

### 3a. Push to GitHub
```bash
git init
git add .
git commit -m "Initial BudgetBot"
gh repo create BudgetBot --public --push
# OR manually push to github.com
```

### 3b. Create Railway Project
1. Go to https://railway.app → **New Project**
2. Select **Deploy from GitHub repo** → choose your repo
3. Railway auto-detects the Dockerfile ✅

### 3c. Set Environment Variables in Railway
In Railway dashboard → your service → **Variables** tab, add:

| Variable | Value |
|----------|-------|
| `Claude__ApiKey` | `sk-ant-XXXXXXXXXX` |
| `PORT` | `8080` |

> **Note:** Railway uses `__` as the separator for nested config.
> `Claude__ApiKey` maps to `Claude:ApiKey` in appsettings.json.

### 3d. Add a Persistent Volume (Important for SQLite!)
1. In Railway → your service → **Volumes** tab
2. Add volume → Mount Path: `/data`
3. This ensures your budget data survives redeploys

### 3e. Get Your Public URL
In Railway → your service → **Settings** → **Networking** → you'll see a URL like:
```
https://budgetagent-production.up.railway.app
```

---

## Step 4 — Connect Twilio to Your Server

1. Go to Twilio Console → **Messaging** → **Sandbox Settings**
2. Set **"When a message comes in"** webhook to:
   ```
   https://YOUR-RAILWAY-URL.up.railway.app/api/webhook/whatsapp
   ```
3. Method: `HTTP POST`
4. Save

---

## Step 5 — Test It!

Open WhatsApp and message your Twilio sandbox number:

```
You: Hi

Bot: 👋 Hey! I'm BudgetBot. No budget set for March yet.
     Set one with: "set budget 30000"

You: set budget 30000

Bot: ✅ Budget set to ₹30,000 for March!

You: spent 850 on lunch at Subway

Bot: ✅ Added ₹850 — Lunch at Subway [Food]
     Spent: ₹850 | Remaining: ₹29,150

You: paid electricity bill 1200

Bot: ✅ Added ₹1,200 — Electricity Bill [Bills]
     Spent: ₹2,050 | Remaining: ₹27,950

You: suggest how to save more this month

Bot: 📊 Your top spending: Food ₹850, Bills ₹1,200
     💡 Tips: Pack lunch 3x/week to save ~₹2,000...
```

---

## Project Structure

```
BudgetAgent/
├── Controllers/
│   └── TwilioWebhookController.cs  ← Receives WhatsApp messages
├── Services/
│   ├── ClaudeService.cs            ← Claude API + tool-use logic
│   └── BudgetService.cs            ← Database read/write operations
├── Models/
│   └── Models.cs                   ← UserBudget, Transaction, ConversationMessage
├── Data/
│   └── AppDbContext.cs             ← EF Core SQLite context
├── Program.cs                      ← App startup, DI registration
├── appsettings.json                ← Config (keys go in Railway env vars)
├── Dockerfile                      ← Railway build instructions
└── README.md                       ← This file
```

---

## Example Conversations

### Setting Budget
> "My budget this month is 25000" / "set budget to 30k" / "budget ₹50000"

### Recording Expenses
> "spent 200 on tea" / "paid 5000 rent" / "bought groceries for 1500" / "petrol 800"

### Recording Income
> "got 5000 as gift" / "received salary 45000"

### Checking Status
> "how much left?" / "show my budget" / "summary" / "what have I spent?"

### Getting Suggestions
> "suggest better budgeting" / "where am I overspending?" / "tips to save more"

---

## Upgrading to Production Twilio (from Sandbox)

When you want a real WhatsApp number (not sandbox):
1. Apply for WhatsApp Business API in Twilio Console
2. Get approved (takes 1–3 days)
3. Update the webhook URL (same as above)

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Bot not replying | Check Railway logs for errors |
| "Claude API key not configured" | Set `Claude__ApiKey` in Railway Variables |
| Data lost after redeploy | Add Railway Volume mounted at `/data` |
| Twilio 11200 error | Your webhook returned non-XML — check logs |
| Messages doubling | Check you don't have two webhooks set |
