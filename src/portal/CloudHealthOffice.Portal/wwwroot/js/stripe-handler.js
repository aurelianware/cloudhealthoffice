// Stripe handler for Cloud Health Office signup
window.StripeHandler = {
    stripe: null,
    cardElement: null,

    initialize: function(publishableKey) {
        // Check if Stripe is already loaded
        if (typeof Stripe === 'undefined') {
            console.error('Stripe.js not loaded');
            return 'Stripe.js failed to load. Please disable any ad blockers and reload the page.';
        }

        // Initialize Stripe only once
        if (!this.stripe) {
            try {
                this.stripe = Stripe(publishableKey);
            } catch (ex) {
                console.error('Stripe() constructor failed:', ex);
                return 'Payment system could not be initialized: ' + ex.message;
            }

            const elements = this.stripe.elements();

            this.cardElement = elements.create('card', {
                style: {
                    base: {
                        fontSize: '16px',
                        color: '#333',
                        '::placeholder': { color: '#aab7c4' }
                    },
                    invalid: { color: '#c00' }
                }
            });

            // Mount the card element
            const cardElementDiv = document.getElementById('card-element');
            if (cardElementDiv) {
                this.cardElement.mount('#card-element');
            } else {
                console.error('Card element div not found');
                return 'Payment form element not found. Please reload the page.';
            }

            // Listen for card validation errors
            this.cardElement.on('change', function(event) {
                const displayError = document.getElementById('card-errors');
                if (displayError) {
                    displayError.textContent = event.error ? event.error.message : '';
                }
            });
        }

        return null; // null = success
    },

    createPaymentMethod: async function(name, email) {
        //console.log('Creating payment method for:', name, email);
        
        if (!this.stripe || !this.cardElement) {
            console.error('Stripe not initialized');
            return JSON.stringify({ 
                success: false, 
                error: 'Payment system not initialized. Please refresh the page.' 
            });
        }

        try {
            const { paymentMethod, error } = await this.stripe.createPaymentMethod({
                type: 'card',
                card: this.cardElement,
                billing_details: {
                    name: name,
                    email: email
                }
            });

            if (error) {
                console.error('Stripe payment method error:', error);
                return JSON.stringify({ 
                    success: false, 
                    error: error.message 
                });
            }

            console.log('Payment method created:', paymentMethod.id);
            return JSON.stringify({ 
                success: true, 
                paymentMethodId: paymentMethod.id 
            });
        } catch (ex) {
            //console.error('Exception creating payment method:', ex);
            return JSON.stringify({ 
                success: false, 
                error: 'Failed to process payment information: ' + ex.message 
            });
        }
    }
};
