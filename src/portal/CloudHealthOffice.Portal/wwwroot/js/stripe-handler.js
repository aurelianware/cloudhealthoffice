// Stripe handler for Cloud Health Office signup
window.StripeHandler = {
    stripe: null,
    cardElement: null,

    initialize: function(publishableKey) {
        console.log('Initializing Stripe with key:', publishableKey.substring(0, 10) + '...');
        
        // Check if Stripe is already loaded
        if (typeof Stripe === 'undefined') {
            console.error('Stripe.js not loaded');
            return;
        }

        // Initialize Stripe only once
        if (!this.stripe) {
            this.stripe = Stripe(publishableKey);
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
                console.log('Card element mounted successfully');
            } else {
                console.error('Card element div not found');
            }

            // Listen for card validation errors
            this.cardElement.on('change', function(event) {
                const displayError = document.getElementById('card-errors');
                if (displayError) {
                    if (event.error) {
                        displayError.textContent = event.error.message;
                    } else {
                        displayError.textContent = '';
                    }
                }
            });
        } else {
            console.log('Stripe already initialized');
        }
    },

    createPaymentMethod: async function(name, email) {
        console.log('Creating payment method for:', name, email);
        
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
            console.error('Exception creating payment method:', ex);
            return JSON.stringify({ 
                success: false, 
                error: 'Failed to process payment information: ' + ex.message 
            });
        }
    }
};
