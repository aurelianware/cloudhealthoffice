FROM hapiproject/hapi:v7.4.0

# Copy configuration
COPY application.yaml /app/config/application.yaml

# Copy synthetic data loader script
COPY load-synthea.sh /app/load-synthea.sh
RUN chmod +x /app/load-synthea.sh

ENV SPRING_CONFIG_LOCATION=file:///app/config/application.yaml
ENV HAPI_FHIR_ALLOW_EXTERNAL_REFERENCES=true

EXPOSE 8080

ENTRYPOINT ["/app/load-synthea.sh"]
