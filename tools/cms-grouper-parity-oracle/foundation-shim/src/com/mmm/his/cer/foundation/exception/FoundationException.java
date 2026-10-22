package com.mmm.his.cer.foundation.exception;

public class FoundationException extends Exception {

  private static final long serialVersionUID = 1L;

  public FoundationException(String message) {
    super(message);
  }

  public FoundationException(String message, Throwable cause) {
    super(message, cause);
  }
}
