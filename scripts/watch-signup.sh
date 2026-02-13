#!/bin/bash
# Real-time signup debugging script

echo "==================================================================="
echo "Cloud Health Office - Signup Form Debugging"
echo "==================================================================="
echo ""
echo "This script will tail the portal logs and filter for signup events."
echo "Open https://portal.cloudhealthoffice.com/signup in your browser,"
echo "fill out the form with test card 4242 4242 4242 4242, and click"
echo "'Start Free Trial'. You should see events appear below in real-time."
echo ""
echo "Press Ctrl+C to stop watching logs."
echo ""
echo "==================================================================="
echo "Watching for signup events..."
echo "==================================================================="
echo ""

kubectl logs -n cloudhealthoffice -l app=portal -f --tail=0 | grep -iE "signup|payment|stripe|tenant"
