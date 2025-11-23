import inquirer from "inquirer";
import { execSync } from "child_process";

export async function runOnboardingWizard() {
  console.log("Welcome to Cloud Health Office CLI Onboarding!");

  const responses = await inquirer.prompt([
    {
      type: "input",
      name: "companyName",
      message: "Enter your company name:",
      validate: (input) => input.trim().length > 0
    },
    {
      type: "input",
      name: "contactEmail",
      message: "Enter your contact email:",
      validate: (input) =>
        /^\S+@\S+\.\S+$/.test(input.trim()) || "Please enter a valid email."
    },
    {
      type: "confirm",
      name: "azureSandbox",
      message: "Would you like to deploy a pre-configured Azure sandbox?",
      default: true
    }
  ]);

  try {
    if (responses.azureSandbox) {
      console.log("Deploying Azure sandbox...");
      // Simulate Azure deployment
      execSync("echo Deploying Azure... (stub)", { stdio: "inherit" });
      // Replace with Azure CLI or Bicep script, etc.
    }
    console.log("Company registered:", responses.companyName);
    console.log("Contact email:", responses.contactEmail);
    // Extend: Simulate 837 claim generation, add PHI redaction in logs, etc.
    console.log("Onboarding complete! See docs at: https://github.com/aurelianware/cloudhealthoffice/wiki/Onboarding");
  } catch (err) {
    console.error("Error during onboarding:", err);
    console.error("Please check your Azure credentials or visit docs for troubleshooting.");
  }
}

if (require.main === module) {
  runOnboardingWizard();
}